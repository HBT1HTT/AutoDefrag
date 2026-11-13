using System.IO;

namespace AutoDefrag.Rewrite;

/// <summary>一次整理（或 dry-run）的完整计划。</summary>
public sealed class RewritePlan
{
    public required string Drive { get; init; }
    public required VolumeGeometry Geometry { get; init; }
    public VolumeLayout? Volume { get; init; }
    public required AnalysisResult Analysis { get; init; }
    public required RewriteOptions Options { get; init; }
    public List<CandidateFile> Candidates { get; init; } = new();
    public List<CandidateFile> Selected { get; init; } = new();
    public Dictionary<string, int> PrecheckSkips { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public long PlannedBytes { get; set; }
}

/// <summary>
/// 候选文件过滤、预检与排序。
/// 排序目标：碎片浪费最多、区段最多、体积适中、非系统，再受“单批上限”约束。
/// </summary>
public static class RewritePlanner
{
    public static RewritePlan? Build(string drive, RewriteOptions options, WorkerStateFile? state = null)
    {
        state?.SetStage("分析碎片", $"正在读取 {drive} 的碎片与空闲布局");
        var analysis = VolumeAnalyzer.Analyze(drive, options);
        if (analysis is null) return null;

        var plan = new RewritePlan
        {
            Drive = drive,
            Geometry = analysis.Geometry,
            Volume = analysis.Volume,
            Analysis = analysis,
            Options = options
        };

        var clusterBytes = analysis.Geometry.ClusterBytes;
        var effectiveReclaim = EffectiveReclaimBytes(options);
        var candidates = new List<CandidateFile>();
        foreach (var file in analysis.Files)
        {
            if (IsCandidate(file, options, clusterBytes, effectiveReclaim))
            {
                candidates.Add(Score(file, options));
                continue;
            }

            // 只因碎片率没达到用户设定阈值而排除的，单独计数，写进日志/报告“未达阈值”
            if (options.MinFragmentationPercent > 0 &&
                file.FragmentationPercent < options.MinFragmentationPercent &&
                IsCandidateExceptFrag(file, options, clusterBytes, effectiveReclaim))
            {
                AddSkip(plan, $"未达碎片率阈值(<{options.MinFragmentationPercent:0.#}%)");
            }
            else if (file.ExtentCount >= 2 && file.FragmentedClusters >= 1 && !file.HasSparse &&
                     file.TotalClusters >= 2 && file.FragmentedClusters * (long)clusterBytes < EffectiveReclaimFor(file, options, clusterBytes, effectiveReclaim))
            {
                // 可回收空间低于下限：这是覆盖率提升的主要来源之一，明确计数而不是静默丢弃
                AddSkip(plan, $"可回收碎片不足(<{DiskService.FormatBytes(EffectiveReclaimFor(file, options, clusterBytes, effectiveReclaim))})");
            }
        }

        plan.Candidates.AddRange(
            candidates
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Layout.FragmentationPercent)
                .ThenByDescending(c => c.Layout.SizeBytes));

        if (options.MinFragmentationPercent > 0)
            Logger.Info($"{plan.Drive} 碎片率阈值={options.MinFragmentationPercent:0.#}%（文件级），未达阈值的文件已跳过并记录");

        var freeClusters = analysis.Volume?.FreeClusters ?? analysis.Geometry.FreeClusters;
        var freeBytes = freeClusters * (long)analysis.Geometry.ClusterBytes;
        var reserveBytes = Math.Max(16L, freeClusters / 200) * analysis.Geometry.ClusterBytes;
        var batchLimitBytes = Math.Max(0, freeBytes - reserveBytes);

        long plannedBytes = 0;
        var attempts = 0;

        foreach (var candidate in plan.Candidates)
        {
            if (plan.Selected.Count >= options.MaxFiles) break;
            if (plannedBytes >= options.MaxBytes) break;
            if (attempts >= options.MaxFiles * 5) break;
            attempts++;

            if (plannedBytes + candidate.Layout.SizeBytes > options.MaxBytes) continue;
            if (plannedBytes + candidate.Layout.SizeBytes > batchLimitBytes)
            {
                AddSkip(plan, "空间不足");
                continue;
            }

            var reason = Precheck(candidate.Layout.Path);
            if (reason is not null)
            {
                AddSkip(plan, reason);
                Logger.Info($"预检跳过 {candidate.Layout.Path}: {reason}");
                continue;
            }

            plan.Selected.Add(candidate);
            plannedBytes += candidate.Layout.SizeBytes;
        }

        plan.PlannedBytes = plannedBytes;
        Logger.Info(
            $"{plan.Drive} 计划: 候选={plan.Candidates.Count}, 选中={plan.Selected.Count}, " +
            $"合计={DiskService.FormatBytes(plannedBytes)}, dry-run={options.DryRun}" +
            (options.RelaxedFilters ? "（补扫轮：已放宽最小文件/可回收门槛）" : string.Empty) +
            (effectiveReclaim == options.MinReclaimBytes ? string.Empty : $"，可回收下限临时放宽到 {DiskService.FormatBytes(effectiveReclaim)}"));
        return plan;
    }

    /// <summary>补扫轮把“可回收不足”的下限放宽到 256KB（仍不低于 1 个簇）。</summary>
    private static long EffectiveReclaimBytes(RewriteOptions options)
    {
        if (!options.RelaxedFilters) return Math.Max(0, options.MinReclaimBytes);
        var relaxed = Math.Max(0, options.RelaxedMinReclaimBytes);
        return options.MinReclaimBytes <= 0
            ? relaxed
            : Math.Min(options.MinReclaimBytes, relaxed);
    }

    /// <summary>
    /// 单个文件实际采用的可回收下限：
    /// Windows /A 只统计 ≤64MB 的文件，这类文件即使只有 1 个簇的尾巴也会让 /A 记一笔碎片，
    /// 所以在这类卷上把它们的下限放宽到 1 个簇；更大的文件仍按配置门槛，避免为几 MB 收益重写几十 GB。
    /// </summary>
    private static long EffectiveReclaimFor(FileLayout layout, RewriteOptions options, int clusterBytes, long baseReclaim)
    {
        if (options.WindowsFragmentationFocus && layout.SizeBytes <= RewriteOptions.WindowsAnalyzedFileMaxBytes)
            return Math.Min(baseReclaim, clusterBytes);
        return baseReclaim;
    }

    /// <summary>能否作为重写候选（不含运行时占用/权限检查）。</summary>
    private static bool IsCandidate(FileLayout layout, RewriteOptions options, int clusterBytes, long minReclaimBytes)
    {
        if (layout.ExtentCount < 2) return false;
        if (layout.FragmentedClusters < 1) return false;
        if (layout.HasSparse) return false;
        if (layout.TotalClusters < 2) return false;
        if (layout.FragmentationPercent < options.MinFragmentationPercent) return false;
        var floor = EffectiveReclaimFor(layout, options, clusterBytes, minReclaimBytes);
        if (layout.FragmentedClusters * (long)clusterBytes < floor) return false;
        return true;
    }

    /// <summary>除“碎片率门槛”之外的硬条件是否满足。</summary>
    private static bool IsCandidateExceptFrag(FileLayout layout, RewriteOptions options, int clusterBytes, long minReclaimBytes)
    {
        var floor = EffectiveReclaimFor(layout, options, clusterBytes, minReclaimBytes);
        return layout.ExtentCount >= 2
            && layout.FragmentedClusters >= 1
            && !layout.HasSparse
            && layout.TotalClusters >= 2
            && layout.FragmentedClusters * (long)clusterBytes >= floor;
    }

    private static CandidateFile Score(FileLayout layout, RewriteOptions options)
    {
        var extentGain = Math.Min(layout.ExtentCount - 1, 20) * 2;
        var copyCost = (long)(layout.TotalClusters * 0.02);
        var sizeBias = layout.TotalClusters >= 2048 ? 20 : 0;
        var score = layout.FragmentedClusters + extentGain + sizeBias - copyCost;

        // /A 口径加成：先处理 ≤64MB 的多区段文件，直接降低 Windows 报告的总碎片空间。
        // 这类文件通常重写成本低，放在大文件前面也避免大文件把单轮预算吃光。
        var aRelevant = options.WindowsFragmentationFocus &&
                        layout.SizeBytes <= RewriteOptions.WindowsAnalyzedFileMaxBytes &&
                        layout.ExtentCount >= 2;
        if (aRelevant) score += 100_000;

        var megabytes = layout.SizeBytes / (1024.0 * 1024.0);
        var reason =
            $"可回收 {layout.FragmentedClusters} 簇, 区段 {layout.ExtentCount}, " +
            $"大小 {megabytes:0.#}MB, 碎片率 {layout.FragmentationPercent:0.##}%" +
            (aRelevant ? "（Windows /A 计入）" : string.Empty);

        return new CandidateFile { Layout = layout, Score = score, ScoreReason = reason };
    }

    /// <summary>执行前预检：属性、硬链接、占用、路径。返回 null 表示通过。</summary>
    private static string? Precheck(string path)
    {
        if (path.Length > 240) return "路径过长";

        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) { return "读取属性失败: " + ex.Message; }

        if ((attributes & FileAttributes.ReadOnly) != 0) return "只读文件";
        if (VolumeAnalyzer.IsUnsupportedAttribute(attributes, out var attributeReason)) return attributeReason;

        var links = NtfsNative.TryGetLinkCount(path);
        if (links > 1) return $"硬链接文件（{links} 个链接）";

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            _ = stream.Length;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return "文件正在使用";
        }
        catch (UnauthorizedAccessException)
        {
            return "没有访问权限";
        }
        catch (Exception ex)
        {
            return "无法独占打开: " + ex.Message;
        }

        return null;
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code == 32 || ex.Message.Contains("being used", StringComparison.OrdinalIgnoreCase)
                           || ex.Message.Contains("正由另一进程使用", StringComparison.Ordinal);
    }

    public static void AddSkip(RewritePlan plan, string reason)
    {
        plan.PrecheckSkips.TryGetValue(reason, out var count);
        plan.PrecheckSkips[reason] = count + 1;
    }
}
