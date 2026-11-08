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
            var drives = driveText.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

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
            var thresholdLog = isClean
                ? $", CPU上限={threshold}%, 整理方式={EngineResolver.ModeText(engine)}, 文件碎片率={(minFrag > 0 ? $"≥{minFrag}%" : "不限")}"
                : ", CPU上限=(检查磁盘时不需要)";
            Logger.Info($"后台任务启动: 模式={modeName}({mode}), 磁盘={(drives.Length == 0 ? "(自动)" : string.Join(",", drives))}{thresholdLog}");

            // 任务级互斥：另一个整理/预览任务（含其他实例发起的）正在运行时不再并发操作同一批卷
            if (!TaskGate.TryEnter(out var gateError))
            {
                Logger.Warn($"后台任务未执行：{gateError}");
                return 3;
            }

            var code = isClean
                ? DefragEngine.RunClean(drives, threshold, engine, 10, 10, minFrag)
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
}
