using System.IO;

namespace AutoDefrag.Rewrite;

/// <summary>
/// 大簇卷整理入口：分析 → 计划 → （dry-run 只出报告）→ 单文件逐个重写 → 重新扫描进入下一轮 → 复算 → 汇总。
/// 单文件独立事务；遇到停止标记只停后续文件。多轮轮次/总时长由整理质量预设决定。
/// </summary>
public static class LargeClusterRunner
{
    public static DriveRunResult Run(
        string drive, DiskMediaKind media, int clusterBytes, WorkerStateFile state, bool dryRun,
        string? scanRoot = null, int cpuThreshold = 70, int highSeconds = 10, int lowSeconds = 10,
        int minFragmentPercent = 0, string engineMode = "Auto", DefragPreferences? settings = null)
    {
        var prefs = (settings ?? DefragPreferences.Default).Normalized();
        var startTime = DateTime.Now;

        // 0 = 全部合格候选；1–15 = 只整理文件级碎片率 ≥ 该值的文件（越界值按“不限”处理，界面已做校验）
        var minFrag = minFragmentPercent is >= 1 and <= 15 ? minFragmentPercent : 0;

        var decision = EngineResolver.Describe(engineMode, clusterBytes);

        // 文件大小门槛按 KB 折算成簇数：64KB 簇盘上 4KB 仍然至少 1 簇（NTFS 最小分配单位）。
        var minClusters = Math.Max(1,
            (int)Math.Ceiling(prefs.MinFileSizeKB * 1024.0 / Math.Max(1, clusterBytes)));

        // CPU 守护覆盖全流程：分析/枚举、复制、校验、替换、复核。
        // 命中阈值时 Check() 会原地阻塞，不再读写；回落并稳定后自动继续。
        var guard = new CpuGuard(cpuThreshold, highSeconds, lowSeconds);
        guard.Phase = "分析";
        guard.StopRequested = () => File.Exists(AppPaths.StopFlagFile);

        var lastCpuSave = DateTime.MinValue;
        guard.OnSample = cpu =>
        {
            state.CpuPercent = cpu;
            if ((DateTime.Now - lastCpuSave).TotalSeconds >= 1)
            {
                lastCpuSave = DateTime.Now;
                state.Save();
            }
        };
        guard.OnPause = (phase, cpu) =>
        {
            state.CpuPercent = cpu;
            state.Suspended = guard.PausedCount;
            state.Message = $"CPU {cpu:N1}% 高于阈值 {cpuThreshold}%（持续 {highSeconds}s），已暂停（{phase}）";
            state.Save();
            ProgressLog.Append($"{drive} CPU 过高：{cpu:N1}% > {cpuThreshold}%（持续 {highSeconds}s），暂停（{phase}）");
        };
        guard.OnResume = (phase, cpu) =>
        {
            state.CpuPercent = cpu;
            state.Resumed = guard.ResumedCount;
            state.Message = $"CPU 已回落（{cpu:N1}%），继续（{phase}）";
            state.Save();
            ProgressLog.Append($"{drive} CPU 回落：{cpu:N1}%，继续（{phase}）");
        };

        var maxRounds = Math.Max(1, prefs.MaxRounds);
        var baseOptions = new
        {
            ScanRoot = scanRoot,
            MinFragmentationPercent = minFrag,
            EngineDecision = decision,
            Guard = guard,
            MinFileSizeKB = prefs.MinFileSizeKB,
            MinFileClusters = minClusters,
            MaxFiles = prefs.MaxFilesPerRun,
            MaxBytes = prefs.MaxBytesPerRunGB * 1024L * 1024 * 1024,
            MinReclaimBytes = prefs.MinReclaimMB * 1024L * 1024,
            VerifyMode = prefs.VerifyMode,
            MaxRunMinutes = prefs.MaxRunMinutes
        };

        var options = dryRun
            ? new RewriteOptions
            {
                DryRun = true, ScanSeconds = 60, MaxScanFiles = 150_000, MaxExtentProbes = 12_000,
                ScanRoot = baseOptions.ScanRoot, MinFragmentationPercent = baseOptions.MinFragmentationPercent,
                EngineDecision = baseOptions.EngineDecision, Guard = baseOptions.Guard,
                MinFileSizeKB = baseOptions.MinFileSizeKB, MinFileClusters = baseOptions.MinFileClusters,
                MaxFiles = baseOptions.MaxFiles, MaxBytes = baseOptions.MaxBytes,
                MinReclaimBytes = baseOptions.MinReclaimBytes, VerifyMode = baseOptions.VerifyMode,
                MaxRounds = maxRounds, MaxRunMinutes = baseOptions.MaxRunMinutes
            }
            : new RewriteOptions
            {
                ScanRoot = baseOptions.ScanRoot, MinFragmentationPercent = baseOptions.MinFragmentationPercent,
                EngineDecision = baseOptions.EngineDecision, Guard = baseOptions.Guard,
                MinFileSizeKB = baseOptions.MinFileSizeKB, MinFileClusters = baseOptions.MinFileClusters,
                MaxFiles = baseOptions.MaxFiles, MaxBytes = baseOptions.MaxBytes,
                MinReclaimBytes = baseOptions.MinReclaimBytes, VerifyMode = baseOptions.VerifyMode,
                MaxRounds = maxRounds, MaxRunMinutes = baseOptions.MaxRunMinutes
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
        state.Round = 0;
        state.RoundTotal = dryRun ? 0 : maxRounds;
        state.CoveragePercent = -1;
        state.Save();

        var qualityName = DefragQualityPresets.Match(prefs)?.Name
                          ?? (prefs.Quality == DefragQualityPresets.Custom ? "自定义" : DefragQualityPresets.Get(prefs.Quality).Name);

        Logger.Info($"================ {drive} 大簇卷整理开始（簇={clusterBytes} 字节, 模式={(dryRun ? "dry-run" : "真实整理")}" +
                    (scanRoot is null ? "，范围=整卷" : $"，范围={scanRoot}") +
                    $"，整理质量={qualityName}，文件碎片率={(minFrag > 0 ? $"≥{minFrag}%" : "不限")}，整理方式={decision}） ================");
        Logger.Info($"{drive} 整理参数: 最小文件={prefs.MinFileSizeKB}KB({minClusters}簇)，单轮≤{prefs.MaxFilesPerRun}文件/" +
                    $"{prefs.MaxBytesPerRunGB}GB，可回收下限={prefs.MinReclaimMB}MB，校验={prefs.VerifyMode}，" +
                    $"最多 {maxRounds} 轮，总时长上限={(prefs.MaxRunMinutes == 0 ? "不限" : $"{prefs.MaxRunMinutes} 分钟")}");
        Logger.Info($"{drive} CPU 守护: 整机占用 > {cpuThreshold}% 连续 {highSeconds}s 暂停读写，" +
                    $"≤ {cpuThreshold}% 稳定 {lowSeconds}s 继续（覆盖 分析/复制/校验/替换/复核，每秒采样）");
        ProgressLog.Append($"{drive} 大簇卷整理开始（{(dryRun ? "dry-run" : "真实整理")}）：{decision}，质量={qualityName}");
        ProgressLog.Append($"{drive} 整理参数: ≥{prefs.MinFileSizeKB}KB，单轮≤{prefs.MaxFilesPerRun} 文件/" +
                           $"{prefs.MaxBytesPerRunGB}GB，最多 {maxRounds} 轮，校验={prefs.VerifyMode}");
        ProgressLog.Append($"{drive} CPU 守护: > {cpuThreshold}% 连续 {highSeconds}s 暂停，回落稳定 {lowSeconds}s 继续");

        if (IsSystemVolume(drive))
        {
            Logger.Error($"{drive} 是系统盘：按安全边界，绝不在系统盘做文件级重写。已跳过。");
            return BuildResult(drive, media, clusterBytes, dryRun, null, null,
                $"{drive} 是系统盘：文件级重写只允许在数据卷上执行，本次未做任何操作", "失败", startTime, null, null);
        }

        if (dryRun)
        {
            state.SetStage("分析碎片", $"正在分析 {drive} 的碎片与空闲布局");
            state.Phase = "分析";
            state.Percent = -1;
            state.Save();
            ProgressLog.Append($"{drive} 开始分析卷位图与文件区段（阶段：分析）");
            var previewPlan = RewritePlanner.Build(drive, options, state);
            if (previewPlan is null)
            {
                return BuildResult(drive, media, clusterBytes, dryRun, null, null,
                    $"{drive} 无法读取卷信息（位图/Runlist 接口失败），本次未做任何操作", "失败", startTime, null, null);
            }

            state.CoveragePercent = Coverage(previewPlan) ?? -1;
            ProgressLog.Append($"{drive} 分析完成：候选={previewPlan.Candidates.Count}，选中={previewPlan.Selected.Count}，" +
                               $"计划处理 {DiskService.FormatBytes(previewPlan.PlannedBytes)}，" +
                               $"覆盖率 {state.CoveragePercent:0.#}%（仅已分析文件）");
            var preview = RewriteReport.BuildPreview(previewPlan);
            var path = RewriteReport.Save(previewPlan, preview);
            Logger.Info($"{drive} dry-run 完成：候选={previewPlan.Candidates.Count}, 选中={previewPlan.Selected.Count}, " +
                        $"覆盖率={state.CoveragePercent:0.#}%, 报告={path ?? "(写入失败)"}");
            state.Percent = 100;
            state.Phase = "完成";
            state.SetStage("完成", $"{drive} 预览完成（未改动任何文件）");
            ProgressLog.Append($"{drive} 预览完成（未改动任何文件）");
            return BuildResult(drive, media, clusterBytes, dryRun, previewPlan, null,
                $"{drive} 预览完成：候选 {previewPlan.Candidates.Count} 个，计划处理 {previewPlan.Selected.Count} 个，未改动任何文件（{DiskService.FormatBytes(previewPlan.PlannedBytes)}）",
                "成功", startTime, previewPlan.Analysis.FileFragmentationPercent, null, path,
                guard.PausedCount, guard.ResumedCount, state.CoveragePercent, 0);
        }

        var summary = new RewriteSummary();
        var deadline = prefs.MaxRunMinutes > 0 ? startTime.AddMinutes(prefs.MaxRunMinutes) : DateTime.MaxValue;
        RewritePlan? plan = null;
        VolumeLayout? volumeBefore = null;
        var fragBefore = 0.0;

        for (var round = 1; round <= maxRounds; round++)
        {
            if (round > 1)
            {
                if (File.Exists(options.StopFlagFile))
                {
                    summary.Stopped = true;
                    Logger.Warn("检测到停止标记，不再开始下一轮");
                    ProgressLog.Append($"{drive} 检测到停止标记：第 {round} 轮不再开始");
                    break;
                }

                if (DateTime.Now >= deadline)
                {
                    Logger.Info($"{drive} 达到总时长上限 {prefs.MaxRunMinutes} 分钟，不再开始第 {round} 轮");
                    ProgressLog.Append($"{drive} 达到总时长上限 {prefs.MaxRunMinutes} 分钟，结束后续轮次");
                    break;
                }

                ProgressLog.Append($"{drive} 开始第 {round}/{maxRounds} 轮（重新扫描候选）");
            }

            state.Round = round;
            state.RoundTotal = maxRounds;
            state.SetStage("分析碎片", $"正在分析 {drive} 的碎片（第 {round}/{maxRounds} 轮）");
            state.Phase = "分析";
            state.Percent = -1;
            state.CurrentFile = string.Empty;
            state.FileIndex = 0;
            state.FileTotal = 0;
            state.ProcessedBytes = 0;
            state.TotalBytes = 0;
            state.SpeedBytesPerSec = 0;
            state.Save();

            guard.Phase = "分析";
            var roundPlan = RewritePlanner.Build(drive, options, state);
            if (roundPlan is null)
            {
                if (plan is null)
                {
                    return BuildResult(drive, media, clusterBytes, dryRun, null, null,
                        $"{drive} 无法读取卷信息（位图/Runlist 接口失败），本次未做任何操作", "失败", startTime, null, null);
                }

                Logger.Warn($"{drive} 第 {round} 轮无法读取卷信息，结束后续轮次");
                ProgressLog.Append($"{drive} 第 {round} 轮无法读取卷信息，结束后续轮次");
                break;
            }

            plan = roundPlan;
            summary.Rounds = round;
            if (round == 1)
            {
                volumeBefore = plan.Volume;
                fragBefore = plan.Analysis.FileFragmentationPercent;
            }

            var coverage = Coverage(plan);
            state.CoveragePercent = coverage ?? -1;
            ProgressLog.Append($"{drive} 第 {round}/{maxRounds} 轮分析完成：候选={plan.Candidates.Count}，选中={plan.Selected.Count}，" +
                               $"计划处理 {DiskService.FormatBytes(plan.PlannedBytes)}，覆盖率 {state.CoveragePercent:0.#}%（仅已分析文件）");
            Logger.Info($"{drive} 第 {round}/{maxRounds} 轮计划: 候选={plan.Candidates.Count}, 选中={plan.Selected.Count}, " +
                        $"合计={DiskService.FormatBytes(plan.PlannedBytes)}, 覆盖率={state.CoveragePercent:0.#}%");

            if (plan.Selected.Count == 0)
            {
                if (round == 1)
                {
                    ProgressLog.Append($"{drive} 没有符合条件的文件，本轮结束");
                }
                else
                {
                    Logger.Info($"{drive} 第 {round} 轮已无合格候选，多轮整理结束");
                    ProgressLog.Append($"{drive} 第 {round} 轮已无合格候选，多轮整理结束");
                }
                break;
            }

            var total = plan.Selected.Count;
            var totalBytes = Math.Max(0, plan.PlannedBytes);
            state.FileTotal = total;
            state.TotalBytes = totalBytes;
            state.ProcessedBytes = 0;
            state.Percent = totalBytes > 0 ? 0 : -1;
            state.SpeedBytesPerSec = 0;
            state.Save();
            ProgressLog.Append($"{drive} 第 {round} 轮计划：选中 {total} 个文件，合计 {DiskService.FormatBytes(plan.PlannedBytes)}，开始逐个重写");

            long doneBytes = 0;
            var roundSuccess = 0;
            for (var i = 0; i < total; i++)
            {
                if (File.Exists(options.StopFlagFile))
                {
                    summary.Stopped = true;
                    Logger.Warn("检测到停止标记，停止后续文件：" + options.StopFlagFile);
                    ProgressLog.Append($"{drive} 检测到停止标记：第 {i + 1}/{total} 个文件前停止");
                    break;
                }

                if (DateTime.Now >= deadline)
                {
                    Logger.Info($"{drive} 达到总时长上限 {prefs.MaxRunMinutes} 分钟，停止本轮后续文件");
                    ProgressLog.Append($"{drive} 达到总时长上限 {prefs.MaxRunMinutes} 分钟，停止后续文件");
                    break;
                }

                // 文件之间也检查一次 CPU，避免上一个文件把系统压满后立刻开始下一个
                guard.Phase = "准备";
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
                state.Message = $"第 {round}/{maxRounds} 轮，正在整理 {drive} 第 {i + 1}/{total} 个文件（{DiskService.FormatBytes(fileBytes)}）";
                state.SetStage("正在重写", state.Message);
                ProgressLog.Append($"{drive} [第 {round} 轮 {i + 1}/{total}] 开始：{state.CurrentFile}（{DiskService.FormatBytes(fileBytes)}）");

                var lastSave = DateTime.Now;
                void OnPhase(string phase, long phaseBytes)
                {
                    state.Phase = phase;
                    guard.Phase = phase;
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
                if (result.Status == "成功")
                {
                    summary.RewrittenBytes += result.SizeBytes;
                    roundSuccess++;
                }

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
                    ? $"{drive} [第 {round} 轮 {i + 1}/{total}] 成功：{Path.GetFileName(result.Path)}，区段 {result.BeforeExtentCount}→{result.AfterExtentCount}，用时 {result.ElapsedSeconds:0.0}s"
                    : $"{drive} [第 {round} 轮 {i + 1}/{total}] {result.Status}：{Path.GetFileName(result.Path)}，原因：{result.Reason}");
            }

            if (summary.Stopped) break;

            if (roundSuccess == 0)
            {
                Logger.Info($"{drive} 第 {round} 轮没有成功重写的文件，提前结束多轮整理");
                ProgressLog.Append($"{drive} 第 {round} 轮没有成功重写的文件，提前结束多轮整理");
                break;
            }

            if (round == maxRounds)
            {
                Logger.Info($"{drive} 已达到轮次上限 {maxRounds} 轮");
                break;
            }

            if (DateTime.Now >= deadline)
            {
                Logger.Info($"{drive} 达到总时长上限 {prefs.MaxRunMinutes} 分钟，不再开始下一轮");
                ProgressLog.Append($"{drive} 达到总时长上限 {prefs.MaxRunMinutes} 分钟，结束后续轮次");
                break;
            }
        }

        // 复算必须基于“最后一轮”的扫描结果；这份扫描也是最新鲜的。
        if (plan is null)
        {
            return BuildResult(drive, media, clusterBytes, dryRun, null, null,
                $"{drive} 无法读取卷信息（位图/Runlist 接口失败），本次未做任何操作", "失败", startTime, null, null);
        }

        state.SetStage("分析碎片", $"正在复算 {drive} 的碎片率");
        state.Phase = "分析";
        state.Percent = plan.Selected.Count > 0 ? 100 : -1;
        state.CurrentFile = string.Empty;
        state.Save();
        ProgressLog.Append($"{drive} 各轮处理完毕，开始复算碎片率");
        guard.Phase = "复核";
        guard.Check();
        var volumeAfter = VolumeAnalyzer.ReadVolumeLayout(drive, options);
        var fragAfter = ComputeResidualFragmentation(drive, plan, summary);
        var coverageFinal = Coverage(plan);
        state.CoveragePercent = coverageFinal ?? -1;
        state.Round = Math.Max(1, summary.Rounds);
        state.RoundTotal = maxRounds;

        var content = RewriteReport.BuildResult(plan, summary, volumeBefore, volumeAfter, fragBefore, fragAfter);
        var reportPath = RewriteReport.Save(plan, content);

        var status = summary.Stopped
            ? "已停止"
            : summary.Results.Count == 0
                ? "无可整理文件"
                : summary.Failed == 0 ? "成功" : "部分成功";

        var message =
            $"{drive} 文件级整理完成：{Math.Max(1, summary.Rounds)} 轮，成功 {summary.Succeeded}、失败 {summary.Failed}、跳过 {summary.Skipped}，" +
            $"碎片率 {fragBefore:0.##}% -> {fragAfter:0.##}%，覆盖率 {(coverageFinal is null ? "未知" : $"{coverageFinal:0.#}%")}（仅已分析文件）";

        Logger.Info(message);
        Logger.Info($"报告: {reportPath ?? "(写入失败)"}");
        state.SetStage("完成", message);
        return BuildResult(drive, media, clusterBytes, dryRun, plan, summary, message, status, startTime, fragBefore, fragAfter, reportPath,
            guard.PausedCount, guard.ResumedCount, coverageFinal, summary.Rounds);
    }

    /// <summary>覆盖率口径：已分析文件的簇 / 卷总簇；只代表“分析到的文件”，不是整卷。</summary>
    private static double? Coverage(RewritePlan plan)
    {
        var total = plan.Geometry.TotalClusters;
        if (total <= 0) return null;
        return Math.Round(plan.Analysis.AnalyzedClusters * 100.0 / total, 1);
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
            ScanRoot = plan.Options.ScanRoot,
            Guard = plan.Options.Guard
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
        int suspended = 0, int resumed = 0, double? coverage = null, int rounds = 0)
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
            clusterBytes,
            coverage,
            rounds);
    }
}
