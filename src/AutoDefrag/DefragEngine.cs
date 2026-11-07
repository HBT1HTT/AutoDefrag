using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoDefrag;

/// <summary>
/// 后台执行引擎（在提权 worker 进程里运行）：
///   Clean  = defrag &lt;盘&gt; /O /H，全程 CPU 守护（挂起/恢复），前后各探测一次碎片率；
///   Status = defrag &lt;盘&gt; /A 只读探测碎片率与剩余空间。
/// 与旧 engine.ps1 保持相同的调用参数、结果文件格式与退出码。
/// </summary>
public static class DefragEngine
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly Regex FragKeyRegex = new(@"fragmented\s+space", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PercentValueRegex = new(@"^([\d.,]+)\s*%$", RegexOptions.Compiled);
    private static readonly Regex SizeValueRegex = new(@"^([\d.,]+)\s*(TB|GB|MB|KB|B)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static int RunClean(string[] drives, int cpuThreshold, int highSeconds = 10, int lowSeconds = 10)
    {
        var targets = FilterToFixed(drives);
        if (targets.Count == 0)
        {
            Logger.Error("未指定有效的本地固定磁盘，引擎退出");
            return 2;
        }

        Logger.Info($"================ 引擎启动: 目标={string.Join(",", targets)} CPU阈值={cpuThreshold}% 触发={highSeconds}s/恢复={lowSeconds}s ================");

        var state = new WorkerStateFile
        {
            Mode = "clean",
            Stage = "准备中",
            DriveTotal = targets.Count,
            StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Message = "正在准备整理"
        };
        state.Save();

        var results = new List<DriveRunResult>();

        for (var index = 0; index < targets.Count; index++)
        {
            var drive = targets[index];
            state.Drive = drive;
            state.DriveIndex = index + 1;
            state.Suspended = 0;
            state.Resumed = 0;
            state.Message = $"正在分析 {drive} 的碎片率";
            state.Stage = "分析碎片";
            state.Save();

            var volumeBytes = TotalBytesOf(drive);
            var preFrag = ProbeFragPercent(drive, volumeBytes);

            state.Stage = "正在优化";
            state.Message = $"正在优化 {drive}（第 {index + 1}/{targets.Count} 个磁盘）";
            state.Save();

            var run = RunOneDrive(drive, cpuThreshold, highSeconds, lowSeconds, state);

            state.Stage = "分析碎片";
            state.Message = $"正在复核 {drive} 的碎片率";
            state.Save();
            var postFrag = ProbeFragPercent(drive, volumeBytes);

            results.Add(new DriveRunResult(
                drive, run.ExitCode, run.Suspended, run.Resumed,
                run.StartTime, run.EndTime, run.Status, preFrag, postFrag));
        }

        var okCount = results.Count(r => r.Status == "OK");
        var total = results.Count;
        var summary = string.Join("; ", results.Select(r =>
            $"{r.Drive}(exit={r.ExitCode?.ToString() ?? "--"},挂起={r.Suspended},恢复={r.Resumed},碎片{FormatFrag(r.PreFrag)}->{FormatFrag(r.PostFrag)})"));

        Logger.Info($"全部完成: 成功={okCount}/{total}  累计挂起={results.Sum(r => r.Suspended)}次 恢复={results.Sum(r => r.Resumed)}次   明细: {summary}");
        WriteLastResult(string.Join(",", targets), summary, okCount, total);

        // 与旧脚本一致：Clean 跑完后写入"每日一次"锁
        WriteLastRunDate();

        state.Stage = "完成";
        state.Drive = string.Empty;
        state.Message = okCount == total
            ? $"已完成 {total} 个磁盘"
            : $"结束：成功 {okCount}/{total}";
        state.Save();

        return okCount == total ? 0 : 1;
    }

    public static int RunStatus(string[] drives)
    {
        var targets = drives.Length == 0
            ? FilterToFixed(Array.Empty<string>(), allowAllWhenEmpty: true)
            : FilterToFixed(drives);
        if (targets.Count == 0)
        {
            Logger.Error("未发现可探测的磁盘，引擎退出");
            return 2;
        }

        Logger.Info($"================ 引擎启动(状态探测): 目标={string.Join(",", targets)} ================");

        var state = new WorkerStateFile
        {
            Mode = "status",
            Stage = "状态探测",
            DriveTotal = targets.Count,
            StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Message = "正在探测碎片率"
        };
        state.Save();

        var file = new DriveStatusFile { ProbeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
        foreach (var drive in targets)
        {
            state.Drive = drive;
            state.Message = $"正在探测 {drive}";
            state.Save();

            var info = FixedDriveByLetter(drive);
            var volumeBytes = info is null ? 0 : DiskService.TotalBytes(info);
            var frag = ProbeFragPercent(drive, volumeBytes);
            file.Drives.Add(new DriveStatusEntry
            {
                Drive = drive,
                VolumeSizeGB = volumeBytes > 0 ? Math.Round(volumeBytes / 1024.0 / 1024 / 1024, 2) : null,
                FreeSpaceGB = info is null ? null : Math.Round(SafeFreeBytes(info) / 1024.0 / 1024 / 1024, 2),
                FragPercent = frag,
                Probed = frag is not null
            });
        }

        file.Save();

        var summary = string.Join("; ", file.Drives.Select(d =>
            $"{d.Drive}(碎片={FormatFrag(d.FragPercent)},剩余={d.FreeSpaceGB?.ToString() ?? "--"}GB/共={d.VolumeSizeGB?.ToString() ?? "--"}GB)"));
        Logger.Info("状态探测完成: " + summary);
        WriteLastResult(string.Join(",", targets), summary, file.Drives.Count, file.Drives.Count);

        state.Stage = "完成";
        state.Message = "碎片率探测完成";
        state.Save();

        return 0;
    }

    private static DriveRunResult RunOneDrive(
        string drive, int cpuThreshold, int highSeconds, int lowSeconds, WorkerStateFile state)
    {
        var startTime = DateTime.Now;
        var suspended = 0;
        var resumed = 0;

        Logger.Info($"开始整理 {drive}   参数: /O /H   CPU阈值={cpuThreshold}%");

        System.Diagnostics.Process? process;
        try
        {
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "defrag.exe",
                Arguments = $"{drive} /O /H",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"启动 defrag.exe 失败({drive}): {ex.Message}");
            return new DriveRunResult(drive, null, 0, 0, startTime, DateTime.Now, "LaunchFailed", null, null);
        }

        if (process is null)
        {
            Logger.Error($"启动 defrag.exe 失败({drive}): 进程对象为空");
            return new DriveRunResult(drive, null, 0, 0, startTime, DateTime.Now, "LaunchFailed", null, null);
        }

        Logger.Info($"defrag.exe 已启动, PID={process.Id}");

        var sampler = new SystemCpuSampler();
        sampler.Sample();          // 预热：建立首个采样基准
        Thread.Sleep(1000);

        var phase = "Running";     // Running / Suspended
        var highCount = 0;
        var lowCount = 0;

        state.Suspended = 0;
        state.Resumed = 0;
        state.Message = $"正在优化 {drive}";
        state.Save();

        while (!HasExited(process))
        {
            Thread.Sleep(1000);
            if (HasExited(process)) break;

            var cpu = sampler.Sample();
            state.CpuPercent = Math.Round(cpu, 1);

            if (phase == "Running")
            {
                if (cpu > cpuThreshold)
                {
                    highCount++;
                    if (highCount >= highSeconds)
                    {
                        if (Native.TrySuspend(process.Id, out var error))
                        {
                            suspended++;
                            state.Suspended = suspended;
                            state.Message = $"CPU 过高，已暂停 {drive} 的整理";
                            Logger.Warn($"CPU 连续 > {cpuThreshold}% 达 {highSeconds}s  ->  挂起 defrag (PID={process.Id}, 当前CPU={cpu:N1}%)");
                        }
                        else
                        {
                            Logger.Warn($"挂起 defrag 失败: {error}");
                        }

                        phase = "Suspended";
                        highCount = 0;
                        lowCount = 0;
                    }
                }
                else
                {
                    highCount = 0;
                }
            }
            else
            {
                if (cpu <= cpuThreshold)
                {
                    lowCount++;
                    if (lowCount >= lowSeconds)
                    {
                        if (Native.TryResume(process.Id, out var error))
                        {
                            resumed++;
                            state.Resumed = resumed;
                            state.Message = $"正在优化 {drive}";
                            Logger.Info($"CPU 回落并稳定 {lowSeconds}s  ->  恢复 defrag (PID={process.Id}, 当前CPU={cpu:N1}%)");
                        }
                        else
                        {
                            Logger.Warn($"恢复 defrag 失败: {error}");
                        }

                        phase = "Running";
                        highCount = 0;
                        lowCount = 0;
                    }
                }
                else
                {
                    lowCount = 0;
                }
            }

            state.Save();
        }

        // 防御性：若进程在挂起态下被外部结束，先恢复再收尾
        if (phase == "Suspended")
        {
            Native.TryResume(process.Id, out _);
        }

        try { process.WaitForExit(); } catch { }

        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { }

        var status = exitCode == 0 ? "OK" : "Failed";
        var endTime = DateTime.Now;

        Logger.Info($"{drive} 整理结束: exit={exitCode?.ToString() ?? "--"}, 挂起={suspended}次, 恢复={resumed}次, 用时={(endTime - startTime).TotalSeconds:N0}s");

        return new DriveRunResult(drive, exitCode, suspended, resumed, startTime, endTime, status, null, null);
    }

    private static bool HasExited(System.Diagnostics.Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    /// <summary>执行只读 defrag &lt;drive&gt; /A 并解析碎片率；失败返回 null（界面显示 "--"）。</summary>
    public static double? ProbeFragPercent(string drive, long volumeBytes)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "defrag.exe",
                Arguments = $"{drive} /A",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return ParseFragPercent(output, volumeBytes);
        }
        catch (Exception ex)
        {
            Logger.Warn($"defrag /A 执行失败({drive}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解析 defrag 报告：优先认 "Total fragmented space = N%"；
    /// 若值是容量则按 /卷大小 换算；键名被本地化时退化为报告中第一个 "= N%"。
    /// </summary>
    public static double? ParseFragPercent(string report, long volumeBytes)
    {
        if (string.IsNullOrWhiteSpace(report)) return null;

        double? anyPercent = null;
        foreach (var rawLine in report.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length == 0) continue;

            var isFragKey = FragKeyRegex.IsMatch(key);

            var percentMatch = PercentValueRegex.Match(value);
            if (percentMatch.Success)
            {
                var number = ParseNumber(percentMatch.Groups[1].Value);
                if (isFragKey) return number;
                anyPercent ??= number;
                continue;
            }

            if (isFragKey && volumeBytes > 0)
            {
                var sizeMatch = SizeValueRegex.Match(value);
                if (sizeMatch.Success)
                {
                    var multiplier = sizeMatch.Groups[2].Value.ToUpperInvariant() switch
                    {
                        "TB" => 1099511627776.0,
                        "GB" => 1073741824.0,
                        "MB" => 1048576.0,
                        "KB" => 1024.0,
                        _ => 1.0
                    };
                    var bytes = ParseNumber(sizeMatch.Groups[1].Value) * multiplier;
                    return Math.Round(bytes / volumeBytes * 100, 2);
                }
            }
        }

        return anyPercent;
    }

    private static double ParseNumber(string raw) =>
        double.TryParse(raw.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0.0;

    /// <summary>规范化盘符并只保留本机存在的固定磁盘。</summary>
    private static List<string> FilterToFixed(string[] drives, bool allowAllWhenEmpty = false)
    {
        var fixedDrives = DiskService.GetFixedDrives();
        var fixedLetters = new HashSet<string>(
            fixedDrives.Select(DiskService.Letter), StringComparer.OrdinalIgnoreCase);

        var requested = new List<string>();
        foreach (var entry in drives)
        {
            foreach (var part in Regex.Split(entry ?? string.Empty, @"[,;\s]+"))
            {
                var letter = part.Trim().TrimEnd('\\').ToUpperInvariant();
                if (letter.Length > 0) requested.Add(letter);
            }
        }

        if (requested.Count == 0)
        {
            return allowAllWhenEmpty ? fixedLetters.ToList() : new List<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var letter in requested)
        {
            if (fixedLetters.Contains(letter) && seen.Add(letter))
            {
                result.Add(letter);
            }
            else
            {
                Logger.Warn($"跳过非本地固定磁盘或当前不可用的目标: {letter}");
            }
        }

        return result;
    }

    private static System.IO.DriveInfo? FixedDriveByLetter(string letter) =>
        DiskService.GetFixedDrives()
            .FirstOrDefault(d => string.Equals(DiskService.Letter(d), letter, StringComparison.OrdinalIgnoreCase));

    private static long TotalBytesOf(string drive)
    {
        var info = FixedDriveByLetter(drive);
        return info is null ? 0 : DiskService.TotalBytes(info);
    }

    private static long SafeFreeBytes(System.IO.DriveInfo info)
    {
        try { return info.AvailableFreeSpace; } catch { return 0; }
    }

    private static string FormatFrag(double? value) => value is null ? "--" : $"{value:0.##}%";

    private static void WriteLastResult(string targets, string summary, int okCount, int total)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 目标={targets} 成功={okCount}/{total} 明细={summary}";
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(AppPaths.LastResultFile, line + Environment.NewLine, Utf8NoBom);
        }
        catch (Exception ex)
        {
            Logger.Warn("写 last_result.txt 失败: " + ex.Message);
        }
    }

    private static void WriteLastRunDate()
    {
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(AppPaths.LastRunFile,
                DateTime.Now.ToString("yyyy-MM-dd") + Environment.NewLine, Utf8NoBom);
            Logger.Info("已写入 last_run.txt = " + DateTime.Now.ToString("yyyy-MM-dd") + "（每日一次锁生效）");
        }
        catch (Exception ex)
        {
            Logger.Warn("写 last_run.txt 失败: " + ex.Message);
        }
    }
}

public sealed record DriveRunResult(
    string Drive,
    int? ExitCode,
    int Suspended,
    int Resumed,
    DateTime StartTime,
    DateTime EndTime,
    string Status,
    double? PreFrag,
    double? PostFrag);
