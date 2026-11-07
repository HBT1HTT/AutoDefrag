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

            Logger.Info($"worker 启动: mode={mode}, drives={(drives.Length == 0 ? "(自动)" : string.Join(",", drives))}, threshold={threshold}%");

            var code = string.Equals(mode, "clean", StringComparison.OrdinalIgnoreCase)
                ? DefragEngine.RunClean(drives, threshold)
                : DefragEngine.RunStatus(drives);

            Logger.Info($"worker 结束: exit={code}");
            return code;
        }
        catch (Exception ex)
        {
            Logger.Error("worker 异常: ", ex);
            return 1;
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
