using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AutoDefrag;

/// <summary>
/// 以管理员身份启动同一个 exe 的 worker 模式（--worker），并跟踪其结束。
/// 主进程保持非提权，只有真正调用 defrag 的 worker 需要管理员权限。
/// </summary>
public sealed class WorkerService
{
    private Process? _process;
    private int _pid;

    public string? Mode { get; private set; }
    public bool IsAuto { get; private set; }

    public bool IsRunning
    {
        get
        {
            if (_process is null) return false;
            try { return !_process.HasExited; }
            catch { return ProcessExists(_pid); }
        }
    }

    public bool Start(string mode, string[]? drives, int threshold, bool auto, out string? error, string engine = "Self",
        int minFragmentPercent = 0, DefragPreferences? defrag = null)
    {
        error = null;
        if (IsRunning)
        {
            error = "已有任务正在运行，请等它结束再试";
            return false;
        }

        // 上一个进程对象若还没被回收，先释放，避免句柄在内核对象表里堆积
        if (_process is not null)
        {
            try { _process.Dispose(); } catch { /* 已退出进程的 Dispose 失败不影响新任务 */ }
            _process = null;
        }

        var prefs = defrag?.Normalized();
        var arguments = BuildArguments(mode, drives, threshold, engine, minFragmentPercent, prefs);
        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.ExePath,
            Arguments = arguments,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppPaths.ExeDir
        };

        try
        {
            _process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            error = ex.NativeErrorCode == 1223
                ? "已取消管理员授权，本次未执行"
                : "无法开始整理：" + ex.Message;
            Logger.Warn("启动整理任务失败: " + ex.Message);
            return false;
        }

        if (_process is null)
        {
            error = "无法开始整理";
            return false;
        }

        _pid = _process.Id;
        Mode = mode;
        IsAuto = auto;
        var modeName = mode == "clean" ? "整理磁盘" : "检查磁盘";
        var thresholdText = mode == "clean"
            ? $", CPU上限={threshold}%" + (minFragmentPercent > 0 ? $", 文件碎片率≥{minFragmentPercent}%" : ", 全部整理") +
              (prefs is null ? string.Empty : $", 质量={prefs.Quality}, 最多{prefs.MaxRounds}轮")
            : string.Empty;
        Logger.Info($"已开始后台任务 (PID={_pid}, 模式={modeName}, 自动={auto}, 目标={(drives is { Length: > 0 } ? string.Join(",", drives) : "(自动)")}{thresholdText})");
        return true;
    }

    /// <summary>若 worker 已结束，返回本次动作并清空跟踪状态。</summary>
    public bool TryComplete(out string mode, out bool auto, out int? exitCode)
    {
        mode = Mode ?? string.Empty;
        auto = IsAuto;
        exitCode = null;

        if (_process is null || IsRunning) return false;

        var finished = _process;
        try { exitCode = finished.ExitCode; }
        catch { exitCode = null; }

        try { finished.Dispose(); } catch { /* 进程已结束，Dispose 异常无需上报 */ }
        _process = null;
        _pid = 0;
        Mode = null;
        IsAuto = false;
        return true;
    }

    private static string BuildArguments(string mode, string[]? drives, int threshold, string engine, int minFragmentPercent,
        DefragPreferences? prefs)
    {
        var sb = new StringBuilder("--worker ").Append(mode);
        if (drives is { Length: > 0 })
        {
            sb.Append(" --drives \"").Append(string.Join(",", drives)).Append('"');
        }
        if (mode == "clean")
        {
            var p = prefs ?? DefragPreferences.Default;
            sb.Append(" --threshold ").Append(Math.Clamp(threshold, 10, 100));
            sb.Append(" --engine ").Append(EngineResolver.IsValid(engine) ? engine : "Self");
            sb.Append(" --minfrag ").Append(minFragmentPercent is >= 1 and <= 15 ? minFragmentPercent : 0);
            // 整理质量与 P1/P2 参数：worker 自己做范围夹紧，命令行只负责传递
            sb.Append(" --quality ").Append(p.Quality);
            sb.Append(" --minfilekb ").Append(p.MinFileSizeKB);
            sb.Append(" --maxfiles ").Append(p.MaxFilesPerRun);
            sb.Append(" --maxbytesgb ").Append(p.MaxBytesPerRunGB);
            sb.Append(" --minreclaimmb ").Append(p.MinReclaimMB);
            sb.Append(" --verify ").Append(p.VerifyMode);
            sb.Append(" --rounds ").Append(p.MaxRounds);
            sb.Append(" --maxminutes ").Append(p.MaxRunMinutes);
            sb.Append(" --ssdopt ").Append(p.SsdOptimizeOnly ? "1" : "0");
        }
        else
        {
            // 检查磁盘也要跑 defrag /A 预检，同样需要 CPU 守护阈值
            sb.Append(" --threshold ").Append(Math.Clamp(threshold, 10, 100));
        }
        return sb.ToString();
    }

    private static bool ProcessExists(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
