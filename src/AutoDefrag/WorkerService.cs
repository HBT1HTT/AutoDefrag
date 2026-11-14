using System.ComponentModel;
using System.Diagnostics;

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
        var normalizedDrives = NormalizeDrives(drives, out var driveError);
        if (driveError is not null)
        {
            error = driveError;
            Logger.Warn("拒绝启动整理任务: " + driveError);
            return false;
        }

        var arguments = BuildArgumentList(mode, normalizedDrives, threshold, engine, minFragmentPercent, prefs);
        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.ExePath,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppPaths.ExeDir
        };
        // 逐项传参：由 .NET 负责引号与转义，不再手工拼命令行字符串
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

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

    /// <summary>盘符白名单：只保留“字母 + 冒号”，任何非法项都拒绝整个任务，避免半套参数启动。</summary>
    private static string[]? NormalizeDrives(string[]? drives, out string? error)
    {
        error = null;
        if (drives is not { Length: > 0 }) return drives;

        var normalized = new List<string>();
        foreach (var raw in drives)
        {
            var text = (raw ?? string.Empty).Trim().TrimEnd('\\');
            if (!DiskService.IsValidDriveSpec(text))
            {
                error = $"磁盘参数无效：{raw}（只接受 A: 形式的盘符）";
                return null;
            }

            normalized.Add(text.ToUpperInvariant());
        }

        return normalized.ToArray();
    }

    private static List<string> BuildArgumentList(string mode, string[]? drives, int threshold, string engine,
        int minFragmentPercent, DefragPreferences? prefs)
    {
        var arguments = new List<string> { "--worker", mode };
        if (drives is { Length: > 0 })
        {
            arguments.Add("--drives");
            arguments.Add(string.Join(",", drives));
        }

        if (mode == "clean")
        {
            var p = prefs ?? DefragPreferences.Default;
            arguments.Add("--threshold");
            arguments.Add(Math.Clamp(threshold, 10, 100).ToString());
            arguments.Add("--engine");
            arguments.Add(EngineResolver.IsValid(engine) ? engine : "Self");
            arguments.Add("--minfrag");
            arguments.Add((minFragmentPercent is >= 1 and <= 15 ? minFragmentPercent : 0).ToString());
            // 整理质量与 P1/P2 参数：worker 自己做范围夹紧，命令行只负责传递
            arguments.Add("--quality");
            arguments.Add(p.Quality);
            arguments.Add("--minfilekb");
            arguments.Add(p.MinFileSizeKB.ToString());
            arguments.Add("--maxfiles");
            arguments.Add(p.MaxFilesPerRun.ToString());
            arguments.Add("--maxbytesgb");
            arguments.Add(p.MaxBytesPerRunGB.ToString());
            arguments.Add("--minreclaimmb");
            arguments.Add(p.MinReclaimMB.ToString());
            arguments.Add("--verify");
            arguments.Add(p.VerifyMode);
            arguments.Add("--rounds");
            arguments.Add(p.MaxRounds.ToString());
            arguments.Add("--maxminutes");
            arguments.Add(p.MaxRunMinutes.ToString());
            arguments.Add("--ssdopt");
            arguments.Add(p.SsdOptimizeOnly ? "1" : "0");
            arguments.Add("--mergefree");
            arguments.Add(p.MergeFreeSpace ? "1" : "0");
            arguments.Add("--allowsystemlarge");
            arguments.Add(p.AllowLargeClusterSystem ? "1" : "0");
        }
        else
        {
            // 检查磁盘也要跑 defrag /A 预检，同样需要 CPU 守护阈值
            arguments.Add("--threshold");
            arguments.Add(Math.Clamp(threshold, 10, 100).ToString());
        }

        // 数据目录由主进程指定：提权到另一个管理员账户时也能读写同一份状态/停止标记/结果
        arguments.Add("--datadir");
        arguments.Add(AppPaths.Root);
        return arguments;
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
