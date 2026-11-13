using System.IO;
using System.Text;

namespace AutoDefrag.Rewrite;

/// <summary>
/// 报告输出：把分析、计划、逐文件结果写成纯文本。
/// 只记录路径、大小、区段、状态与原因，不记录任何文件内容。
/// </summary>
public static class RewriteReport
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string BuildPreview(RewritePlan plan)
    {
        var sb = new StringBuilder();
        Header(sb, plan, dryRun: true);

        sb.AppendLine("—— 将按以下顺序处理（本轮只预览，不会改动任何文件）——");
        if (plan.Selected.Count == 0)
        {
            sb.AppendLine("  没有满足条件的候选文件。");
        }

        for (var i = 0; i < plan.Selected.Count; i++)
        {
            var candidate = plan.Selected[i];
            sb.AppendLine($"[{i + 1}/{plan.Selected.Count}] {candidate.Layout.Path}");
            sb.AppendLine($"    大小={DiskService.FormatBytes(candidate.Layout.SizeBytes)}  " +
                          $"区段={candidate.Layout.ExtentCount}  " +
                          $"可回收碎片={candidate.Layout.FragmentedClusters} 簇  " +
                          $"碎片率={candidate.Layout.FragmentationPercent:0.##}%");
            sb.AppendLine($"    排序分数={candidate.Score}  依据: {candidate.ScoreReason}");
        }

        var notSelected = plan.Candidates.Count - plan.Selected.Count;
        if (notSelected > 0)
        {
            sb.AppendLine($"另有 {notSelected} 个候选因单批上限（{plan.Options.MaxFiles} 文件 / {DiskService.FormatBytes(plan.Options.MaxBytes)}）未纳入本次。");
        }

        // 跳过明细：让用户知道“没处理的都在哪、为什么”
        var analysisSkips = plan.Analysis.SkipCounts;
        if (analysisSkips.Count > 0)
        {
            var items = string.Join(", ", analysisSkips.Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendLine($"分析阶段排除（共 {analysisSkips.Values.Sum()} 项）: {items}");
        }

        if (plan.PrecheckSkips.Count > 0)
        {
            var items = string.Join(", ", plan.PrecheckSkips.Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendLine($"计划阶段跳过（共 {plan.PrecheckSkips.Values.Sum()} 项，含未达阈值/占用/空间）: {items}");
        }

        Footer(sb, plan);
        return sb.ToString();
    }

    public static string BuildResult(RewritePlan plan, RewriteSummary summary, VolumeLayout? volumeBefore, VolumeLayout? volumeAfter, double fragBefore, double fragAfter)
    {
        var sb = new StringBuilder();
        Header(sb, plan, dryRun: plan.Options.DryRun);

        sb.AppendLine($"处理前: 文件级碎片率={fragBefore:0.##}%  " +
                      $"空闲簇={(volumeBefore?.FreeClusters ?? plan.Geometry.FreeClusters)}  " +
                      $"空闲区间={(volumeBefore?.FreeRunCount.ToString() ?? "--")}  " +
                      $"最大连续空闲={(volumeBefore?.MaxFreeRunClusters.ToString() ?? "--")} 簇");
        sb.AppendLine($"处理后: 文件级碎片率={fragAfter:0.##}%  " +
                      $"空闲簇={(volumeAfter?.FreeClusters.ToString() ?? "--")}  " +
                      $"空闲区间={(volumeAfter?.FreeRunCount.ToString() ?? "--")}  " +
                      $"最大连续空闲={(volumeAfter?.MaxFreeRunClusters.ToString() ?? "--")} 簇");
        sb.AppendLine();

        sb.AppendLine("—— 逐文件结果 ——");
        if (summary.Results.Count == 0)
        {
            sb.AppendLine("  没有处理任何文件。");
        }

        for (var i = 0; i < summary.Results.Count; i++)
        {
            var r = summary.Results[i];
            sb.AppendLine($"[{i + 1}/{summary.Results.Count}] {r.Path}");
            sb.AppendLine($"    大小={DiskService.FormatBytes(r.SizeBytes)}  结果={r.Status}" +
                          (string.IsNullOrEmpty(r.Reason) ? string.Empty : $"  原因={r.Reason}"));
            sb.AppendLine($"    before: 区段={r.BeforeExtentCount}  [{r.BeforeExtents}]");
            sb.AppendLine($"    after : 区段={r.AfterExtentCount}  [{r.AfterExtents}]");
            if (r.Status == "成功")
            {
                sb.AppendLine($"    可回收碎片 = {r.BeforeFragmentedClusters - r.AfterFragmentedClusters} 簇" +
                              $"  校验={r.HashMode}  用时={r.ElapsedSeconds:0.##}s");
            }
        }

        sb.AppendLine();
        sb.AppendLine("—— 汇总 ——");
        sb.AppendLine($"成功={summary.Succeeded}  失败={summary.Failed}  跳过={summary.Skipped}" +
                      (summary.Stopped ? "  （已按停止标记提前结束）" : string.Empty));
        sb.AppendLine($"重写数据量={DiskService.FormatBytes(summary.RewrittenBytes)}  " +
                      $"回收碎片簇={summary.ReclaimedFragmentedClusters}（约 {DiskService.FormatBytes(summary.ReclaimedFragmentedClusters * plan.Geometry.ClusterBytes)}）");

        var skipMap = summary.SkipReasons;
        if (skipMap.Count > 0)
        {
            var items = string.Join(", ", skipMap.Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendLine("跳过原因（逐文件）: " + items);
        }

        if (plan.PrecheckSkips.Count > 0)
        {
            var items = string.Join(", ", plan.PrecheckSkips.Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendLine("预检跳过（计划阶段）: " + items);
        }

        var analysisSkips = plan.Analysis.SkipCounts;
        if (analysisSkips.Count > 0)
        {
            var items = string.Join(", ", analysisSkips.Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendLine("分析阶段排除: " + items);
        }

        sb.AppendLine();
        sb.AppendLine("说明：本工具只做文件级重写，不移动 NTFS 元数据（$MFT/$Bitmap/$LogFile 等），");
        sb.AppendLine("      也不承诺把碎片整理到 0；结果以“区段减少、碎片簇回收”为准。");
        Footer(sb, plan);
        return sb.ToString();
    }

    /// <summary>把报告写到 Data 目录并返回实际路径（失败返回 null）。</summary>
    public static string? Save(RewritePlan plan, string content)
    {
        var path = plan.Options.DryRun ? AppPaths.RewritePreviewFile : AppPaths.RewriteReportFile;
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(path, content, Utf8NoBom);
            Logger.Info($"报告已写入: {path}");
            return path;
        }
        catch (Exception ex)
        {
            Logger.Warn($"写报告失败({path}): {ex.Message}");
            return null;
        }
    }

    private static void Header(StringBuilder sb, RewritePlan plan, bool dryRun)
    {
        sb.AppendLine("================ AutoDefrag 大簇卷文件级整理报告 ================");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (plan.Options.EngineDecision.Length > 0)
        {
            sb.AppendLine($"整理方式: {plan.Options.EngineDecision}");
        }
        sb.AppendLine($"卷: {plan.Drive}  簇={plan.Geometry.ClusterBytes} 字节  " +
                      $"总簇={plan.Geometry.TotalClusters}  空闲簇={(plan.Volume?.FreeClusters ?? plan.Geometry.FreeClusters)}");

        if (plan.Volume is not null)
        {
            sb.AppendLine($"空闲布局: 区间={plan.Volume.FreeRunCount}  " +
                          $"最大连续={plan.Volume.MaxFreeRunClusters} 簇（起点 LCN {plan.Volume.MaxFreeRunStartLcn}，约 {DiskService.FormatBytes(plan.Volume.MaxFreeRunClusters * plan.Geometry.ClusterBytes)}）  " +
                          $"空闲碎片度={plan.Volume.FreeFragmentationPercent:0.##}%  " +
                          $"可整理上限={plan.Volume.RewriteUpperBoundClusters} 簇");
        }
        else
        {
            sb.AppendLine("空闲布局: 未知（未能读取卷位图）——无法评估空闲连续区与可整理上限；");
            sb.AppendLine("          本次计划只依据文件区段与卷几何，替身文件的位置由 Windows 分配，选择结果不受影响。");
        }

        var analysis = plan.Analysis;
        var coverage = plan.Geometry.TotalClusters > 0
            ? analysis.AnalyzedClusters * 100.0 / plan.Geometry.TotalClusters
            : 0;
        sb.AppendLine($"分析: 枚举={analysis.FilesScanned}  查询Runlist={analysis.FilesProbed}  " +
                      $"读取失败={analysis.FilesUnreadable}  已分析文件={analysis.Files.Count}  " +
                      $"文件级碎片率={analysis.FileFragmentationPercent:0.##}%" + (analysis.ScanTruncated ? "  （抽样：达到扫描上限）" : string.Empty));
        sb.AppendLine($"覆盖率: 已分析簇={analysis.AnalyzedClusters} / 总簇={plan.Geometry.TotalClusters}（{coverage:0.##}%）" +
                      "  —— 碎片率只代表已分析文件的区段，不代表整卷碎片");
        sb.AppendLine($"候选={plan.Candidates.Count}  本次选中={plan.Selected.Count}  " +
                      $"合计={DiskService.FormatBytes(plan.PlannedBytes)}  " +
                      $"模式={(dryRun ? "dry-run（只预览）" : "真实整理")}");
        sb.AppendLine();
    }

    private static void Footer(StringBuilder sb, RewritePlan plan)
    {
        sb.AppendLine();
        sb.AppendLine($"整理范围: {(plan.Options.MinFragmentationPercent > 0
            ? $"只整理文件级碎片率 ≥ {plan.Options.MinFragmentationPercent:0.#}% 的文件（未达阈值的跳过并计入下方统计）"
            : "全部合格候选（不按碎片率过滤）")}");
        sb.AppendLine($"安全余量、单批上限: 文件数<={plan.Options.MaxFiles} 字节数<={DiskService.FormatBytes(plan.Options.MaxBytes)}  " +
                      $"最小文件={(plan.Options.MinFileSizeKB > 0 ? $"{plan.Options.MinFileSizeKB}KB({plan.Options.MinFileClusters}簇)" : $"{plan.Options.MinFileClusters} 簇")}  " +
                      $"最小碎片率={plan.Options.MinFragmentationPercent:0.#}%  最小回收={DiskService.FormatBytes(plan.Options.MinReclaimBytes)}");
        sb.AppendLine($"整理质量参数: 校验={VerifyText(plan.Options.VerifyMode)}  " +
                      $"轮次上限={plan.Options.MaxRounds}  " +
                      $"总时长上限={(plan.Options.MaxRunMinutes <= 0 ? "不限" : $"{plan.Options.MaxRunMinutes} 分钟")}");
        sb.AppendLine("碎片率口径: 只统计本次实际分析到的文件（覆盖率见上方），不代表整卷碎片率；" +
                      "系统文件/MFT/空闲空间布局不在本报告口径内。");
        sb.AppendLine("停止方式: 在运行数据目录创建 rewrite_stop.flag（默认 %LocalAppData%\\AutoDefrag\\Data），当前文件做完后停止继续（已完成的文件不会回滚）。");
        sb.AppendLine("================================================================");
    }

    private static string VerifyText(string mode) => mode switch
    {
        "Full" => "全量 SHA256（最慢，误判最少）",
        "Sample" => "首/中/尾抽样 SHA256（最快，只覆盖 3×4MB）",
        _ => "自动（≤2GB 全量，超过抽样）"
    };
}
