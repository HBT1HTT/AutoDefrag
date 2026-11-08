namespace AutoDefrag.Rewrite;

/// <summary>
/// 自研引擎的 CPU 守护。
/// 系统 defrag 方式靠挂起/恢复 defrag.exe 进程；文件级重写由本进程自己读写，
/// 没有可挂起的子进程，因此改为“暂停读写、等 CPU 回落再继续”，语义一致。
/// 只在文件内的复制/校验循环里周期调用，不产生后台线程。
/// </summary>
public sealed class CpuGuard
{
    private readonly int _threshold;
    private readonly int _highSeconds;
    private readonly int _lowSeconds;
    private readonly SystemCpuSampler _sampler = new();

    private DateTime? _highSince;
    private DateTime? _lowSince;
    private bool _paused;

    public CpuGuard(int threshold, int highSeconds, int lowSeconds)
    {
        _threshold = Math.Clamp(threshold, 10, 100);
        _highSeconds = Math.Max(1, highSeconds);
        _lowSeconds = Math.Max(1, lowSeconds);
    }

    /// <summary>因 CPU 过高暂停的次数。</summary>
    public int PausedCount { get; private set; }

    /// <summary>暂停后继续的次数。</summary>
    public int ResumedCount { get; private set; }

    public double LastCpuPercent { get; private set; }

    /// <summary>
    /// 采样一次；超过阈值持续 highSeconds 就原地等待，回落稳定 lowSeconds 才返回。
    /// 在复制/哈希循环里每 32MB 左右调用一次即可。
    /// </summary>
    public void Check()
    {
        var cpu = _sampler.Sample();
        LastCpuPercent = Math.Round(cpu, 1);

        if (_paused)
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
                    Logger.Info($"CPU 回落并稳定 {_lowSeconds}s（当前 {cpu:N1}%），继续复制");
                    return;
                }
            }
            else
            {
                _lowSince = null;
            }

            Thread.Sleep(1000);
            return;
        }

        if (cpu > _threshold)
        {
            _highSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _highSince.Value).TotalSeconds >= _highSeconds)
            {
                _paused = true;
                PausedCount++;
                Logger.Warn($"CPU 连续 > {_threshold}% 达 {_highSeconds}s（当前 {cpu:N1}%），暂停文件复制");
            }
        }
        else
        {
            _highSince = null;
        }
    }
}
