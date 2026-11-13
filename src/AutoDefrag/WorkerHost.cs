using System.Globalization;

namespace AutoDefrag;

/// <summary>
/// worker 入口：同一个 exe 通过 --worker 参数以提权进程运行，不显示任何窗口。
/// 参数: --worker clean|status  [--drives "C:,A:"]  [--threshold 70]
/// </summary>
public static class WorkerHost
{
    public static bool IsWorkerRequest(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--worker", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static int Execute(string[] args)
    {
        Logger.Init("engine");
        try
        {
            AppPaths.Ensure();

            var mode = GetValue(args, "--worker") ?? "status";
            var driveText = GetValue(args, "--drives") ?? string.Empty;
            var requestedDrives = driveText
                .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim().TrimEnd('\\'))
                .ToArray();
            // 白名单：只接受“字母 + 冒号”，其余参数丢弃并记日志，避免把任意字符串传进后续逻辑
            var drives = requestedDrives
                .Where(DiskService.IsValidDriveSpec)
                .Select(d => d.ToUpperInvariant())
                .ToArray();
            if (drives.Length != requestedDrives.Length)
            {
                Logger.Warn($"忽略 {requestedDrives.Length - drives.Length} 个非法磁盘参数（只接受 A: 形式的盘符）");
            }

            var threshold = 70;
            var thresholdText = GetValue(args, "--threshold");
            if (thresholdText is not null &&
                int.TryParse(thresholdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                threshold = Math.Clamp(parsed, 10, 100);
            }

            var isClean = string.Equals(mode, "clean", StringComparison.OrdinalIgnoreCase);
            var engineText = GetValue(args, "--engine") ?? "Auto";
            var engine = engineText.Equals("System", StringComparison.OrdinalIgnoreCase) ? "System"
                : engineText.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "Auto"
                : "Self";

            // 0 = 不按文件碎片率过滤（全部整理）；1–15 = 只整理达到该碎片率的文件
            var minFrag = 0;
            var minFragText = GetValue(args, "--minfrag");
            if (minFragText is not null &&
                int.TryParse(minFragText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFrag))
            {
                minFrag = parsedFrag is >= 1 and <= 15 ? parsedFrag : 0;
            }

            var modeName = isClean ? "整理磁盘" : "检查磁盘";

            // 整理质量与 P1/P2 参数：worker 自己再规范化一次，防止旧版 UI 或手写命令行传入越界值
            var prefs = new DefragPreferences(
                GetValue(args, "--quality") ?? DefragQualityPresets.Balanced,
                GetInt(args, "--minfilekb", 32),
                GetInt(args, "--maxfiles", 100),
                GetInt(args, "--maxbytesgb", 20),
                GetInt(args, "--minreclaimmb", 4),
                GetValue(args, "--verify") ?? "Auto",
                GetInt(args, "--rounds", 4),
                GetInt(args, "--maxminutes", 180),
                (GetValue(args, "--ssdopt") ?? "1") != "0",
                (GetValue(args, "--mergefree") ?? "1") != "0").Normalized();

            var qualityName = DefragQualityPresets.Match(prefs)?.Name
                              ?? (prefs.Quality == DefragQualityPresets.Custom ? "自定义" : DefragQualityPresets.Get(prefs.Quality).Name);
            var thresholdLog = isClean
                ? $", CPU上限={threshold}%, 整理方式={EngineResolver.ModeText(engine)}, 文件碎片率={(minFrag > 0 ? $"≥{minFrag}%" : "不限")}" +
                  $", 质量={qualityName}, 最小文件={prefs.MinFileSizeKB}KB, 单轮≤{prefs.MaxFilesPerRun}文件/{prefs.MaxBytesPerRunGB}GB" +
                  $", 可回收≥{prefs.MinReclaimMB}MB, 校验={prefs.VerifyMode}, 最多{prefs.MaxRounds}轮, 固态只优化={(prefs.SsdOptimizeOnly ? "是" : "否")}" +
                  $", 机械盘合并空闲={(prefs.MergeFreeSpace ? "是" : "否")}"
                : ", CPU上限=(检查磁盘时不需要)";
            Logger.Info($"后台任务启动: 模式={modeName}({mode}), 磁盘={(drives.Length == 0 ? "(自动)" : string.Join(",", drives))}{thresholdLog}");

            // 任务级互斥：另一个整理/预览任务（含其他实例发起的）正在运行时不再并发操作同一批卷
            if (!TaskGate.TryEnter(out var gateError))
            {
                Logger.Warn($"后台任务未执行：{gateError}");
                return 3;
            }

            var code = isClean
                ? DefragEngine.RunClean(drives, threshold, engine, 10, 10, minFrag, prefs)
                : DefragEngine.RunStatus(drives, threshold, 10, 10);

            Logger.Info($"后台任务结束: exit={code}");
            return code;
        }
        catch (Exception ex)
        {
            Logger.Error("worker 异常: ", ex);
            return 1;
        }
        finally
        {
            TaskGate.Leave();
        }
    }

    private static string? GetValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i + 1];
                return value.StartsWith('-') ? null : value;
            }
        }
        return null;
    }

    private static int GetInt(string[] args, string name, int fallback)
    {
        var text = GetValue(args, name);
        if (text is null) return fallback;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }
}
