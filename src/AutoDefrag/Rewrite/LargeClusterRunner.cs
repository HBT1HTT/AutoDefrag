using System.IO;

namespace AutoDefrag.Rewrite;

/// <summary>
/// 大簇卷整理入口：分析 → 计划 → （dry-run 只出报告）→ 单文件逐个重写 → 复算 → 汇总。
/// 单文件独立事务；遇到停止标记只停后续文件。
/// </summary>
public static class LargeClusterRunner
{
    public static DriveRunResult Run(
        string drive, DiskMediaKind media, int clusterBytes, WorkerStateFile state, bool dryRun,
        string? scanRoot = null, int cpuThreshold = 70, int highSeconds = 10, int lowSeconds = 10,
        int minFragmentPercent = 0, string engineMode = "Auto")
    {
        var startTime = DateTime.Now;

        // 0 = 全部合格候选；1–15 = 只整理文件级碎片率 ≥ 该值的文件（越界值按“不限”处理，界面已做校验）
        var minFrag = minFragmentPercent is >= 1 and <= 15 ? minFragmentPercent : 0;

        var decision = EngineResolver.Describe(engineMode, clusterBytes);
        var options = dryRun
            ? new RewriteOptions
            {
                DryRun = true, ScanSeconds = 60, MaxScanFiles = 150_000, MaxExtentProbes = 12_000,
                ScanRoot = scanRoot, MinFragmentationPercent = minFrag, EngineDecision = decision
            }
            : new RewriteOptions
            {
                ScanRoot = scanRoot, MinFragmentationPercent = minFrag, EngineDecision = decision
            };

        state.Engine = EngineResolver.ShortName(EngineResolver.Effective(engineMode, clusterBytes));
        state.EngineDetail = decision;
        state.Phase = string.Empty;
        state.CurrentFile = string.Empty;
        state.FileIndex = 0;
        state.FileTotal = 0;
        state.ProcessedBytes = 0;
        state.TotalBytes = 0;
        state.SpeedBytesPerSec = 0;
        state.Percent = -1;
        state.Save();

        Logger.Info($"================ {drive} 大簇卷整理开始（簇={clusterBytes} 字节, 模式={(dryRun ? "dry-run" : "真实整理")}" +
                    (scanRoot is null ? "，范围=整卷" : $"，范围={scanRoot}") +
                    $"，文件碎片率={(minFrag > 0 ? $"≥{minFrag}%" : "不限")}，整理方式={decision}） ================");
        ProgressLog.Append($"{drive} 大簇卷整理开始（{(dryRun ? "dry-run" : "真实整理")}）：{decision}");

        if (IsSystemVolume(drive))
        {
            Logger.Error($"{drive} 是系统盘：按安全边界，绝不在系统盘做文件级重写。已跳过。");
            return BuildResult(drive, media, clusterBytes, dryRun, null, null,
                $"{drive} 是系统盘：文件级重写只允许在数据卷上执行，本次未做任何操作", "失败", startTime, null, null);
        }

        state.SetStage("分析碎片", $"正在分析 {drive} 的碎片与空闲布局");
        state.Phase = "分析";
        state.Percent = -1;
        state.Save();
        ProgressLog.Append($"{drive} 开始分析卷位图与文件区段（阶段：分析）");
        var plan = RewritePlanner.Build(drive, options, state);
        if (plan is null)
        {
            return BuildResult(drive, media, clusterBytes, dryRun, null, null,
                $"{drive} 无法读取卷信息（位图/Runlist 接口失败），本次未做任何操作", "失败", startTime, null, null);
        }

        var volumeBefore = plan.Volume;
        var fragBefore = plan.Analysis.FileFragmentationPercent;

        if (dryRun)
        {
            ProgressLog.Append($"{drive} 分析完成：候选={plan.Candidates.Count}，选中={plan.Selected.Count}，" +
                               $"计划处理 {DiskService.FormatBytes(plan.PlannedBytes)}");
            var preview = RewriteReport.BuildPreview(plan);
            var path = RewriteReport.Save(plan, preview);
            Logger.Info($"{drive} dry-run 完成：候选={plan.Candidates.Count}, 选中={plan.Selected.Count}, 报告={path ?? "(写入失败)"}");
            state.Percent = 100;
            state.Phase = "完成";
            state.SetStage("完成", $"{drive} 预览完成（未改动任何文件）");
            ProgressLog.Append($"{drive} 预览完成（未改动任何文件）");
            return BuildResult(drive, media, clusterBytes, dryRun, plan, null,
                $"{drive} 预览完成：候选 {plan.Candidates.Count} 个，计划处理 {plan.Selected.Count} 个，未改动任何文件（{DiskService.FormatBytes(plan.PlannedBytes)}）",
                "成功", startTime, fragBefore, null, path);
        }

        var summary = new RewriteSummary();
        var guard = new CpuGuard(cpuThreshold, highSeconds, lowSeconds);
        var total = plan.Selected.Count;
        var totalBytes = Math.Max(0, plan.PlannedBytes);
        state.FileTotal = total;
        state.TotalBytes = totalBytes;
        state.ProcessedBytes = 0;
        state.Percent = totalBytes > 0 ? 0 : -1;
        state.SpeedBytesPerSec = 0;
        state.Save();
        ProgressLog.Append($"{drive} 计划：选中 {total} 个文件，合计 {DiskService.FormatBytes(plan.PlannedBytes)}，开始逐个重写");

        long doneBytes = 0;
        for (var i = 0; i < total; i++)
        {
            if (File.Exists(options.StopFlagFile))
            {
                summary.Stopped = true;
                Logger.Warn("检测到停止标记，停止后续文件：" + options.StopFlagFile);
                ProgressLog.Append($"{drive} 检测到停止标记：第 {i + 1}/{total} 个文件前停止");
                break;
            }

            // 文件之间也检查一次 CPU，避免上一个文件把系统压满后立刻开始下一个
            guard.Check();

            var candidate = plan.Selected[i];
            var fileBytes = candidate.Layout.SizeBytes;
            var fileStarted = DateTime.Now;
            state.Drive = drive;
            state.DriveLabel = drive.TrimEnd(':');
            state.FileIndex = i + 1;
            state.CurrentFile = Path.GetFileName(candidate.Layout.Path);
            state.Phase = "准备";
            state.ProcessedBytes = doneBytes;
            state.Percent = totalBytes > 0 ? (int)Math.Min(99, doneBytes * 100.0 / totalBytes) : -1;
            state.Message = $"正在整理 {drive} 第 {i + 1}/{total} 个文件（{DiskService.FormatBytes(fileBytes)}）";
            state.SetStage("正在重写", state.Message);
            ProgressLog.Append($"{drive} [{i + 1}/{total}] 开始：{state.CurrentFile}（{DiskService.FormatBytes(fileBytes)}）");

            var lastSave = DateTime.Now;
            void OnPhase(string phase, long phaseBytes)
            {
                state.Phase = phase;
                var current = doneBytes + Math.Min(Math.Max(0, phaseBytes), fileBytes);
                state.ProcessedBytes = current;
                if (totalBytes > 0)
                {
                    state.Percent = (int)Math.Min(99, current * 100.0 / totalBytes);
                }

                var elapsed = Math.Max(1.0, (DateTime.Now - fileStarted).TotalSeconds);
                state.SpeedBytesPerSec = (long)((current - doneBytes) / elapsed);

                if ((DateTime.Now - lastSave).TotalMilliseconds >= 800)
                {
                    lastSave = DateTime.Now;
                    state.Save();
                }
            }

            var result = RewriteExecutor.Execute(candidate.Layout.Path, plan, guard, OnPhase);
            summary.Results.Add(result);
            if (result.Status == "成功") summary.RewrittenBytes += result.SizeBytes;

            doneBytes += fileBytes;
            state.ProcessedBytes = doneBytes;
            state.Phase = string.Empty;
            state.CurrentFile = string.Empty;
            state.Percent = totalBytes > 0 ? (int)Math.Min(99, doneBytes * 100.0 / totalBytes) : -1;
            state.SpeedBytesPerSec = (long)(doneBytes / Math.Max(1.0, (DateTime.Now - startTime).TotalSeconds));
            state.CpuPercent = guard.LastCpuPercent;
            state.Suspended = guard.PausedCount;
            state.Resumed = guard.ResumedCount;
            state.Save();

            ProgressLog.Append(result.Status == "成功"
                ? $"{drive} [{i + 1}/{total}] 成功：{Path.GetFileName(result.Path)}，区段 {result.BeforeExtentCount}→{result.AfterExtentCount}，用时 {result.ElapsedSeconds:0.0}s"
                : $"{drive} [{i + 1}/{total}] {result.Status}：{Path.GetFileName(result.Path)}，原因：{result.Reason}");
        }

        state.SetStage("分析碎片", $"正在复算 {drive} 的碎片率");
        state.Phase = "分析";
        state.Percent = totalBytes > 0 ? 100 : -1;
        state.CurrentFile = string.Empty;
        state.Save();
        ProgressLog.Append($"{drive} 全部文件处理完毕，开始复算碎片率");
        var volumeAfter = VolumeAnalyzer.ReadVolumeLayout(drive, options);
        var fragAfter = ComputeResidualFragmentation(drive, plan, summary);

        var content = RewriteReport.BuildResult(plan, summary, volumeBefore, volumeAfter, fragBefore, fragAfter);
        var reportPath = RewriteReport.Save(plan, content);

        var exitCode = summary.Failed == 0 ? 0 : 1;
        var status = summary.Stopped
            ? "已停止"
            : summary.Results.Count == 0
                ? "无可整理文件"
                : summary.Failed == 0 ? "成功" : "部分成功";

        var message =
            $"{drive} 文件级整理完成：成功 {summary.Succeeded}、失败 {summary.Failed}、跳过 {summary.Skipped}，" +
            $"碎片率 {fragBefore:0.##}% -> {fragAfter:0.##}%";

        Logger.Info(message);
        Logger.Info($"报告: {reportPath ?? "(写入失败)"}");
        state.SetStage("完成", message);
        return BuildResult(drive, media, clusterBytes, dryRun, plan, summary, message, status, startTime, fragBefore, fragAfter, reportPath,
            guard.PausedCount, guard.ResumedCount);
    }

    /// <summary>整理后按“同一扫描范围”再算一次碎片率，避免拿整卷抽样结果和目录结果比较。</summary>
    private static double ComputeResidualFragmentation(string drive, RewritePlan plan, RewriteSummary summary)
    {
        var quick = VolumeAnalyzer.Analyze(drive, new RewriteOptions
        {
            ScanSeconds = 30,
            MaxScanFiles = 150_000,
            MaxExtentProbes = 12_000,
            MinFragmentationPercent = 0,
            MinFileClusters = 1,
            ScanRoot = plan.Options.ScanRoot
        });

        if (quick is not null) return quick.FileFragmentationPercent;

        var before = plan.Analysis.AnalyzedClusters;
        if (before <= 0) return 0.0;
        var reclaimed = summary.ReclaimedFragmentedClusters;
        return Math.Max(0, (plan.Analysis.AnalyzedFragmentedClusters - reclaimed) * 100.0 / before);
    }

    /// <summary>系统盘（Windows 所在卷）不做文件级重写。</summary>
    public static bool IsSystemVolume(string drive)
    {
        var letter = drive.TrimEnd('\\').ToUpperInvariant();
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var windowsRoot = Path.GetPathRoot(windows) ?? string.Empty;
            if (!string.IsNullOrEmpty(windowsRoot) &&
                string.Equals(windowsRoot.TrimEnd('\\'), letter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // 忽略：下面还有系统盘环境变量的兜底判断
        }

        try
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? string.Empty;
            return string.Equals(systemDrive.TrimEnd('\\'), letter, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;   // 判断不了就按系统盘处理，宁可不做
        }
    }

    private static DriveRunResult BuildResult(
        string drive, DiskMediaKind media, int clusterBytes, bool dryRun, RewritePlan? plan, RewriteSummary? summary,
        string message, string status, DateTime startTime, double? fragBefore, double? fragAfter, string? reportPath = null,
        int suspended = 0, int resumed = 0)
    {
        var arguments = dryRun ? "文件级重写(dry-run)" : "文件级重写";
        Logger.Info($"{drive} {arguments} 结束: 状态={status} 说明={message}" + (reportPath is null ? string.Empty : $" 报告={reportPath}"));

        return new DriveRunResult(
            drive, media, arguments,
            status == "失败" ? 1 : 0,
            suspended, resumed, startTime, DateTime.Now,
            status,
            fragBefore,
            fragAfter,
            clusterBytes);
    }
}
