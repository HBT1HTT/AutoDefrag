using System.IO;

namespace AutoDefrag.Rewrite;

/// <summary>
/// 命令行入口：
///   AutoDefrag.exe --dry-run [--drives "A:"] [--quiet]   只读预览大簇卷整理计划
/// </summary>
public static class RewriteCommand
{
    public static bool IsDryRunRequest(string[] args) =>
        args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase)) ||
        args.Any(a => string.Equals(a, "--rewrite", StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        Logger.Init("engine");
        AppPaths.Ensure();

        var quiet = args.Any(a => string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase));
        var dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));
        var path = ParsePath(args);
        var drives = ParseDrives(args);

        if (drives.Count == 0)
        {
            if (path is not null)
            {
                var root = Path.GetPathRoot(path) ?? string.Empty;
                if (root.Length >= 2) drives.Add(root.TrimEnd('\\').ToUpperInvariant());
            }
            else
            {
                drives = DiskService.GetFixedDrives()
                    .Select(DiskService.Letter)
                    .Where(letter => DiskService.IsLargeCluster(Native.GetClusterSize(letter)))
                    .ToList();
            }
        }

        if (drives.Count == 0)
        {
            Logger.Warn("没有找到簇大小大于 4KB 的本地固定磁盘");
            if (!quiet)
            {
                System.Windows.MessageBox.Show(
                    "没有找到每块大于 4KB 的磁盘，这类磁盘才使用文件级整理。\n\n" +
                    "也可以指定盘符：AutoDefrag.exe --dry-run --drives \"A:\"",
                    "AutoDefrag 预览", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            return 2;
        }

        return RunDrives(drives, dryRun, quiet, path);
    }

    public static int RunDrives(IReadOnlyList<string> drives, bool dryRun, bool quiet, string? scanRoot = null)
    {
        AppPaths.Ensure();
        ProgressLog.Begin();

        // 任务级互斥：整理 worker 或另一实例的预览正在运行时，绝不再并发操作卷
        if (!TaskGate.TryEnter(out var gateError))
        {
            Logger.Warn($"未执行：{gateError}");
            ProgressLog.Append($"未执行：{gateError}");
            if (!quiet)
            {
                System.Windows.MessageBox.Show(
                    gateError + "。\n\n请等当前任务结束后再试。",
                    "AutoDefrag 预览", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            return 3;
        }

        try
        {
            return RunDrivesCore(drives, dryRun, quiet, scanRoot);
        }
        finally
        {
            TaskGate.Leave();
        }
    }

    private static int RunDrivesCore(IReadOnlyList<string> drives, bool dryRun, bool quiet, string? scanRoot)
    {
        // 与界面共用同一份设置：预览/命令行整理都按“只整理碎片较多的文件”设置来过滤
        var config = new ConfigStore().Load(out _);
        var minFrag = config.FragThresholdEnabled ? config.FragThresholdPercent : 0;
        var engineMode = EngineResolver.IsValid(config.EngineMode) ? config.EngineMode : EngineResolver.Auto;
        var cpuThreshold = config.CpuThreshold;
        Logger.Info($"文件级整理参数: 文件碎片率={(minFrag > 0 ? $"≥{minFrag}%" : "不限")} " +
                    $"整理方式={EngineResolver.ModeText(engineMode)} CPU守护={cpuThreshold}%（连续10秒暂停/稳定10秒继续）");
        ProgressLog.Append($"开始{(dryRun ? "预览" : "文件级整理")}：目标={string.Join(",", drives)}；" +
                           $"整理方式={EngineResolver.ModeText(engineMode)}；CPU守护={cpuThreshold}%");

        var lines = new List<string>();
        var failed = 0;

        foreach (var drive in drives)
        {
            var letter = drive.TrimEnd('\\').ToUpperInvariant();
            if (!letter.EndsWith(':')) letter += ":";

            var clusterBytes = Native.GetClusterSize(letter);
            if (!DiskService.IsLargeCluster(clusterBytes))
            {
                lines.Add($"{letter} 每块 {DiskService.ClusterText(clusterBytes)}（≤4KB），仍由系统 defrag.exe 处理，不在本次文件级重写范围内");
                Logger.Info($"{letter} 簇={clusterBytes}，跳过文件级重写");
                ProgressLog.Append($"{letter} 跳过：存储块 {DiskService.ClusterText(clusterBytes)}，不属于文件级重写范围");
                continue;
            }

            var info = DiskService.GetFixedDrives()
                .FirstOrDefault(d => string.Equals(DiskService.Letter(d), letter, StringComparison.OrdinalIgnoreCase));
            var media = info is null ? DiskMediaKind.Unknown : DiskService.GetMediaKind(info);
            var state = new WorkerStateFile
            {
                Mode = dryRun ? "rewrite-preview" : "rewrite",
                Stage = "准备中",
                Drive = letter,
                DriveLabel = letter.TrimEnd(':'),
                DriveIndex = 1,
                DriveTotal = drives.Count,
                StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Message = "正在准备文件级整理",
                Percent = -1,
                Engine = EngineResolver.ShortName(EngineResolver.Effective(engineMode, clusterBytes)),
                EngineDetail = EngineResolver.Describe(engineMode, clusterBytes)
            };
            state.Save();

            var result = LargeClusterRunner.Run(letter, media, clusterBytes, state, dryRun, scanRoot,
                cpuThreshold: cpuThreshold, highSeconds: 10, lowSeconds: 10,
                minFragmentPercent: minFrag, engineMode: engineMode);
            if (result.Status == "失败") failed++;
            var fragText = dryRun
                ? $"碎片 {FormatFrag(result.PreFrag)} → 未改动（dry-run，整理后未测量）"
                : result.PostFrag is null
                    ? $"碎片 {FormatFrag(result.PreFrag)} → 未复核"
                    : $"碎片 {FormatFrag(result.PreFrag)} → {FormatFrag(result.PostFrag)}";
            lines.Add($"{letter.TrimEnd(':')}: {result.Status}，{fragText}");
        }

        var summary = string.Join(Environment.NewLine, lines);
        Logger.Info("文件级整理/预览汇总:" + Environment.NewLine + summary);

        if (!quiet)
        {
            var report = dryRun ? AppPaths.RewritePreviewFile : AppPaths.RewriteReportFile;
            System.Windows.MessageBox.Show(
                (dryRun ? "预览完成（未改动任何文件）" : "整理完成") + Environment.NewLine + Environment.NewLine +
                summary + Environment.NewLine + Environment.NewLine +
                "详细报告：" + report,
                "AutoDefrag", System.Windows.MessageBoxButton.OK,
                failed > 0 ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Information);
        }

        return failed > 0 ? 1 : 0;
    }

    private static string? ParsePath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--path", StringComparison.OrdinalIgnoreCase)) continue;
            var value = args[i + 1];
            return Directory.Exists(value) ? Path.GetFullPath(value) : null;
        }
        return null;
    }

    private static List<string> ParseDrives(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--drives", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var part in args[i + 1].Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var letter = part.Trim().TrimEnd('\\').ToUpperInvariant();
                if (letter.Length == 2 && letter[1] == ':') result.Add(letter);
            }
            break;
        }

        return result;
    }

    private static string FormatFrag(double? value) => value is null ? "--" : $"{value:0.##}%";
}
