using System.Threading;

namespace AutoDefrag.Rewrite;

/// <summary>
/// CPU 守护（整机占用，GetSystemTimes）。
/// 语义：整机 CPU 连续 highSeconds 秒高于阈值 → 当前线程原地暂停（不再读写）；
/// 回落到阈值以下并连续 lowSeconds 秒 → 自动继续。默认每秒采样一次。
/// 系统 defrag 方式靠挂起/恢复 defrag.exe；文件级重写没有子进程可挂起，
/// 因此改为在分析、复制、校验、替换、复核各阶段周期性调用 Check()，命中即原地等待。
/// </summary>
public sealed class CpuGuard
{
    private readonly int _threshold;
    private readonly int _highSeconds;
    private readonly int _lowSeconds;
    private readonly int _sampleIntervalMs;
    private readonly Func<double> _sample;

    private DateTime? _highSince;
    private DateTime? _lowSince;
    private bool _paused;
    private DateTime _lastSampleAt = DateTime.MinValue;
    private double _lastCpu;

    /// <param name="sampler">采样源；默认整机 CPU。测试可注入固定序列。</param>
    /// <param name="sampleIntervalMs">采样间隔，默认 1000ms。</param>
    public CpuGuard(int threshold, int highSeconds, int lowSeconds,
        Func<double>? sampler = null, int sampleIntervalMs = 1000)
    {
        _threshold = Math.Clamp(threshold, 10, 100);
        _highSeconds = Math.Max(1, highSeconds);
        _lowSeconds = Math.Max(1, lowSeconds);
        _sampleIntervalMs = Math.Max(1, sampleIntervalMs);

        if (sampler is not null)
        {
            _sample = sampler;
        }
        else
        {
            var system = new SystemCpuSampler();
            _sample = system.Sample;
        }
    }

    /// <summary>因 CPU 过高暂停的次数。</summary>
    public int PausedCount { get; private set; }

    /// <summary>暂停后继续的次数。</summary>
    public int ResumedCount { get; private set; }

    public double LastCpuPercent { get; private set; }

    /// <summary>当前是否处于暂停等待中。</summary>
    public bool IsPaused => _paused;

    /// <summary>当前阶段（分析/复制/校验/替换/复核），只用于日志与状态展示。</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>每次采样回调（用于把 CPU 实时值写进 worker_state）。</summary>
    public Action<double>? OnSample;

    /// <summary>进入暂停回调（阶段, 当前 CPU）。</summary>
    public Action<string, double>? OnPause;

    /// <summary>恢复继续回调（阶段, 当前 CPU）。</summary>
    public Action<string, double>? OnResume;

    /// <summary>暂停等待期间检查用户是否请求停止；返回 true 则提前结束等待。</summary>
    public Func<bool>? StopRequested;

    /// <summary>
    /// 采样一次并判定。命中暂停条件时本调用会原地阻塞，直到 CPU 回落稳定或收到停止请求；
    /// 因此调用方在复制/校验/枚举循环里周期性调用即可，不需要额外状态机。
    /// </summary>
    public void Check()
    {
        var cpu = Sample();

        if (_paused)
        {
            if (TryResume(cpu)) return;
            WaitWhilePaused(cpu);
            return;
        }

        if (cpu > _threshold)
        {
            _highSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _highSince.Value).TotalSeconds >= _highSeconds)
            {
                _paused = true;
                _lowSince = null;
                PausedCount++;
                var phase = PhaseText();
                Logger.Warn($"CPU 连续 > {_threshold}% 达 {_highSeconds}s（当前 {cpu:N1}%，阶段={phase}），暂停文件读写（第 {PausedCount} 次）");
                OnPause?.Invoke(phase, cpu);
                WaitWhilePaused(cpu);
            }
            return;
        }

        _highSince = null;
    }

    private double Sample()
    {
        // 采样节流：调用方可能每秒调用上千次（每个目录/每 256 个文件），
        // 但 GetSystemTimes 的差分间隔太短会得到 0 或抖动值，导致高负载计数被反复清零。
        // 因此最多每 sampleIntervalMs 真正采样一次，其余调用复用上次结果。
        var now = DateTime.UtcNow;
        if (_lastSampleAt != DateTime.MinValue &&
            (now - _lastSampleAt).TotalMilliseconds < _sampleIntervalMs)
        {
            return _lastCpu;
        }

        _lastSampleAt = now;
        var cpu = _sample();
        if (double.IsNaN(cpu) || cpu is < 0 or > 100) cpu = 0;
        _lastCpu = cpu;
        LastCpuPercent = Math.Round(cpu, 1);
        OnSample?.Invoke(LastCpuPercent);
        return cpu;
    }

    /// <summary>暂停中采到低值并稳定 lowSeconds → 恢复；返回是否已恢复。</summary>
    private bool TryResume(double cpu)
    {
        if (cpu <= _threshold)
        {
            _lowSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _lowSince.Value).TotalSeconds >= _lowSeconds)
            {
                _paused = false;
                _lowSince = null;
                _highSince = null;
                ResumedCount++;
                var phase = PhaseText();
                Logger.Info($"CPU 回落并稳定 {_lowSeconds}s（当前 {cpu:N1}%，阶段={phase}），继续文件读写（第 {ResumedCount} 次）");
                OnResume?.Invoke(phase, cpu);
                return true;
            }
        }
        else
        {
            _lowSince = null;
        }

        return false;
    }

    /// <summary>暂停期间原地等待：每秒采样，直到回落稳定或用户停止。</summary>
    private void WaitWhilePaused(double cpu)
    {
        var lastStatusLog = DateTime.UtcNow;
        while (_paused)
        {
            if (StopRequested?.Invoke() == true)
            {
                Logger.Info("暂停等待中收到停止请求，结束等待（保持已暂停状态，不再开始新的文件）");
                return;
            }

            Thread.Sleep(_sampleIntervalMs);

            var next = Sample();
            if (TryResume(next)) return;

            // 长时间高负载时每 30 秒留一条现场记录，便于判断“卡住”还是“主动暂停”
            if ((DateTime.UtcNow - lastStatusLog).TotalSeconds >= 30)
            {
                lastStatusLog = DateTime.UtcNow;
                Logger.Info($"仍在暂停：当前 {next:N1}% > 阈值 {_threshold}%（阶段={PhaseText()}）");
            }
        }
    }

    private string PhaseText() => Phase.Length > 0 ? Phase : "文件读写";
}
