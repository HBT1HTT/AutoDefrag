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

    public bool Start(string mode, string[]? drives, int threshold, bool auto, out string? error)
    {
        error = null;
        if (IsRunning)
        {
            error = "已有任务正在运行，请等它结束再试";
            return false;
        }

        var arguments = BuildArguments(mode, drives, threshold);
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
                : "启动后台引擎失败：" + ex.Message;
            Logger.Warn("启动 worker 失败: " + ex.Message);
            return false;
        }

        if (_process is null)
        {
            error = "启动后台引擎失败";
            return false;
        }

        _pid = _process.Id;
        Mode = mode;
        IsAuto = auto;
        Logger.Info($"已拉起后台引擎 (PID={_pid}, Mode={mode}, Auto={auto}, 目标={(drives is { Length: > 0 } ? string.Join(",", drives) : "(自动)")}, 阈值={threshold}%)");
        return true;
    }

    /// <summary>若 worker 已结束，返回本次动作并清空跟踪状态。</summary>
    public bool TryComplete(out string mode, out bool auto, out int? exitCode)
    {
        mode = Mode ?? string.Empty;
        auto = IsAuto;
        exitCode = null;

        if (_process is null || IsRunning) return false;

        try { exitCode = _process.ExitCode; }
        catch { exitCode = null; }

        _process = null;
        _pid = 0;
        Mode = null;
        IsAuto = false;
        return true;
    }

    private static string BuildArguments(string mode, string[]? drives, int threshold)
    {
        var sb = new StringBuilder("--worker ").Append(mode);
        if (drives is { Length: > 0 })
        {
            sb.Append(" --drives \"").Append(string.Join(",", drives)).Append('"');
        }
        if (mode == "clean")
        {
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
