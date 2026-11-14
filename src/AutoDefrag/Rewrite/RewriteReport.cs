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

    public static string BuildPreview(RewritePlan plan, MergeGate? mergeGate = null)
    {
        var sb = new StringBuilder();
        Header(sb, plan, dryRun: true);

        if (mergeGate is not null)
        {
            sb.AppendLine("—— 机械硬盘空闲空间合并（/X）评估 ——");
            sb.AppendLine($"  {(mergeGate.Allowed ? "满足门槛：真实整理时会先执行 defrag <盘> /X，再搬文件" : "本次不执行")}");
            sb.AppendLine($"  原因: {mergeGate.Reason}");
            sb.AppendLine($"  门槛: 碎片率 ≥ {DefragEngine.MergeFreeSpaceFragThreshold:0}%，空闲空间占比 ≥ {DefragEngine.MergeFreeSpaceFreeRatio:0}% 且 ≥ {DiskService.FormatBytes(DefragEngine.MergeFreeSpaceMinFreeBytes)}，空闲碎片度 ≥ {DefragEngine.MergeFreeSpaceFreeFragThreshold:0}%；仅机械硬盘");
            sb.AppendLine("  顺序: 先合并空闲空间再重写文件；先重写会把空闲空间重新打散，合并收益会被抵消。");
            sb.AppendLine();
        }

        sb.AppendLine("—— 将按以下顺序处理（本轮只预览，不会改动任何文件）——");
        if (DefragPolicy.IgnoresSystemNoAction(plan.Geometry.ClusterBytes))
        {
            sb.AppendLine($"  注意: 本卷簇大小 {plan.Geometry.ClusterBytes} 字节 > {DefragPolicy.SystemDefragClusterLimit}；" +
                          "即使 Windows /A 显示“无需整理”，也不会跳过文件级重写。");
        }
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

    public static string BuildResult(RewritePlan plan, RewriteSummary summary, VolumeLayout? volumeBefore, VolumeLayout? volumeAfter,
        double fragBefore, double fragAfter, MergeGate? mergeGate = null, MergeRunResult? mergeRun = null,
        ExternalFragResult? winBefore = null, ExternalFragResult? winAfter = null, RewritePlan? firstPlan = null)
    {
        var sb = new StringBuilder();
        Header(sb, plan, dryRun: plan.Options.DryRun);
        var beforePlan = firstPlan ?? plan;

        sb.AppendLine("—— 对外口径（Windows 自带 defrag /A，唯一可信的整卷碎片率）——");
        if (winBefore?.FragPercent is not null || winAfter?.FragPercent is not null)
        {
            sb.AppendLine($"整理前: {FragText(winBefore)}");
            sb.AppendLine($"整理后: {FragText(winAfter)}");
            if (winBefore?.FragPercent is not null && winAfter?.FragPercent is not null)
            {
                sb.AppendLine($"变化: {winBefore.FragPercent:0.##}% -> {winAfter.FragPercent:0.##}%");
            }
        }
        else
        {
            sb.AppendLine("未取得可用数值：defrag /A 在该卷上失败或被跳过（详见下方原因）。");
            sb.AppendLine($"  整理前: exit={winBefore?.ExitCode?.ToString() ?? "未执行"}, 错误码={winBefore?.ErrorCode ?? "无"}");
            sb.AppendLine($"  整理后: exit={winAfter?.ExitCode?.ToString() ?? "未执行"}, 错误码={winAfter?.ErrorCode ?? "无"}");
            sb.AppendLine("  说明：本报告不会用“已分析文件碎片率”冒充整卷碎片率。");
        }
        if (DefragPolicy.IgnoresSystemNoAction(plan.Geometry.ClusterBytes))
        {
            sb.AppendLine($"  策略：本卷簇大小 {plan.Geometry.ClusterBytes} 字节 > {DefragPolicy.SystemDefragClusterLimit}，" +
                          "Windows /A 的“无需整理”不作为跳过依据，文件级重写照常执行。");
        }
        sb.AppendLine();
        sb.AppendLine("—— 本程序口径（只代表分析到的文件，不是整卷）——");
        var totalClusters = Math.Max(1, plan.Geometry.TotalClusters);
        var freeClusters = volumeBefore?.FreeClusters ?? plan.Geometry.FreeClusters;
        var usedClusters = Math.Max(1, totalClusters - freeClusters);
        var coverageBefore = beforePlan.Analysis.AnalyzedClusters * 100.0 / totalClusters;
        var coverageAfter = plan.Analysis.AnalyzedClusters * 100.0 / totalClusters;
        var coverageUsedBefore = beforePlan.Analysis.AnalyzedClusters * 100.0 / usedClusters;
        var coverageUsedAfter = plan.Analysis.AnalyzedClusters * 100.0 / usedClusters;
        var aRelevant = beforePlan.Analysis.Files.Count(f => f.SizeBytes <= RewriteOptions.WindowsAnalyzedFileMaxBytes);
        var aRelevantFrag = beforePlan.Analysis.Files.Count(f =>
            f.SizeBytes <= RewriteOptions.WindowsAnalyzedFileMaxBytes && f.ExtentCount >= 2 && f.FragmentedClusters >= 1);
        var aRelevantSelected = beforePlan.Candidates.Count(c =>
            c.Layout.SizeBytes <= RewriteOptions.WindowsAnalyzedFileMaxBytes);
        sb.AppendLine($"处理前: 已分析文件碎片率={fragBefore:0.##}%  卷覆盖率={coverageBefore:0.##}%  已用数据覆盖率={coverageUsedBefore:0.##}%");
        sb.AppendLine($"处理后: 已分析文件碎片率={fragAfter:0.##}%  卷覆盖率={coverageAfter:0.##}%  已用数据覆盖率={coverageUsedAfter:0.##}%");
        sb.AppendLine($"空闲簇={(volumeBefore?.FreeClusters ?? plan.Geometry.FreeClusters)} -> {(volumeAfter?.FreeClusters.ToString() ?? "--")}  " +
                      $"空闲区间={(volumeBefore?.FreeRunCount.ToString() ?? "--")} -> {(volumeAfter?.FreeRunCount.ToString() ?? "--")}  " +
                      $"最大连续空闲={(volumeBefore?.MaxFreeRunClusters.ToString() ?? "--")} -> {(volumeAfter?.MaxFreeRunClusters.ToString() ?? "--")} 簇");
        sb.AppendLine();
        sb.AppendLine("口径对照（为什么两个数字差很多）:");
        sb.AppendLine("  口径            | 分子                                 | 分母         | 本次");
        sb.AppendLine($"  Windows /A      | Windows 计为“碎片”的空间（≤64MB 文件） | 整卷          | {winBefore?.FragPercent?.ToString("0.##") ?? "--"}% -> {winAfter?.FragPercent?.ToString("0.##") ?? "--"}%");
        sb.AppendLine($"  本程序碎片率    | 已分析文件的可回收碎片簇              | 已分析文件的簇 | {fragBefore:0.##}% -> {fragAfter:0.##}%");
        sb.AppendLine($"  本程序覆盖率    | 已分析文件的簇                        | 整卷 / 已用簇  | {coverageBefore:0.##}% -> {coverageAfter:0.##}% / {coverageUsedBefore:0.##}% -> {coverageUsedAfter:0.##}%");
        sb.AppendLine("  关系：覆盖率说明“本程序看了多少”，碎片率说明“看到的部分有多碎”；/A 的分母是整卷，且只统计 ≤64MB 的文件，");
        sb.AppendLine("        所以 /A 的残留里永远包含本工具不处理的部分，覆盖率越低越不能把两个数字直接对比。");
        sb.AppendLine($"  /A 口径候选：≤64MB 文件 {aRelevant} 个，其中多区段 {aRelevantFrag} 个；本次计划选中 {aRelevantSelected} 个（总候选 {beforePlan.Candidates.Count} 个）。");
        sb.AppendLine("  “大于 64MB 的文件碎片不会计入”是 Windows 输出里的原话；均衡/最彻底档会优先处理 ≤64MB 的多区段文件，");
        sb.AppendLine("  但这也意味着：/A 的 9% 不一定来自可重写的文件碎片，可能来自空闲空间/元数据/Windows 自己的统计方式。");
        sb.AppendLine();

        if (mergeGate is not null || mergeRun is not null)
        {
            sb.AppendLine("—— 机械硬盘空闲空间合并（/X）——");
            sb.AppendLine($"执行情况: {mergeRun?.Status ?? (mergeGate?.Allowed == true ? "未执行" : "未启用")}");
            sb.AppendLine($"原因/结果: {mergeRun?.Detail ?? mergeGate?.Reason ?? "未启用"}");
            if (mergeRun is not null)
            {
                sb.AppendLine($"命令: defrag.exe {plan.Drive} /X /H  exit={mergeRun.ExitCode?.ToString() ?? "未知"}  " +
                              $"错误码={mergeRun.ErrorCode ?? "无"}  用时={mergeRun.ElapsedSeconds:0}s  " +
                              $"暂停={mergeRun.Suspended}次 继续={mergeRun.Resumed}次");
                sb.AppendLine("顺序说明: 先 /X 再重写文件。先重写产生的临时文件/备份会把空闲空间重新打散，/X 放在后面收益会被抵消。");
            }
            sb.AppendLine($"门槛: 仅机械硬盘；碎片率 ≥ {DefragEngine.MergeFreeSpaceFragThreshold:0}%、空闲空间占比 ≥ {DefragEngine.MergeFreeSpaceFreeRatio:0}% 且 ≥ {DiskService.FormatBytes(DefragEngine.MergeFreeSpaceMinFreeBytes)}、空闲碎片度 ≥ {DefragEngine.MergeFreeSpaceFreeFragThreshold:0}%。不满足时只写原因，不执行长时间搬移。");
            sb.AppendLine("失败处理: 非 0 退出码、权限不足（0x89000024）、卷被占用都会标为失败/未成功，不会被记成成功；失败后继续文件级重写，最终状态标注“部分成功”。");
            sb.AppendLine();
        }

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
            // 只有真正尝试过新位置（成功或回滚）时才打印 after；跳过时若有试算结果也如实标为“新位置”，
            // 避免出现 after: 区段=0 [] 这种会让人误以为数据丢失的误导输出。
            if (!string.IsNullOrEmpty(r.AfterExtents) || r.AfterExtentCount > 0)
            {
                var label = r.Status == "跳过" ? "新位置" : "after ";
                sb.AppendLine($"    {label}: 区段={r.AfterExtentCount}  [{r.AfterExtents}]");
            }
            if (r.Status == "成功")
            {
                sb.AppendLine($"    可回收碎片 = {r.BeforeFragmentedClusters - r.AfterFragmentedClusters} 簇" +
                              $"  校验={r.HashMode}  用时={r.ElapsedSeconds:0.##}s");
            }
        }

        sb.AppendLine();
        sb.AppendLine("—— 汇总 ——");
        sb.AppendLine($"成功={summary.Succeeded}  失败={summary.Failed}  跳过={summary.Skipped}" +
                      $"  轮次={Math.Max(1, summary.Rounds)}（补扫 {summary.RelaxedRounds} 轮）" +
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
        sb.AppendLine("—— 仍然存在且本工具无法消除的碎片来源（/A 的残留就来自这里）——");
        sb.AppendLine("  · $MFT、$LogFile 等 NTFS 元数据：本工具不移动、不重写；");
        sb.AppendLine("  · pagefile.sys、hiberfil.sys：系统占用，无法独占打开；");
        sb.AppendLine("  · 注册表 hive（\\Windows\\System32\\config 等）：系统持续写入；");
        sb.AppendLine("  · WinSxS、Windows 目录、系统恢复/引导目录：为安全起见不纳入候选（数据盘上的 Program Files 会纳入，但只读/被占用/受保护文件仍跳过）；");
        sb.AppendLine("  · 正在被进程占用的文件、没有访问权限的文件：跳过并计入上方统计；");
        sb.AppendLine("  · 加密/稀疏/压缩/重解析点/硬链接文件：重写会破坏语义或链接，明确不处理；");
        sb.AppendLine("  · 新建/正在增长的文件、系统更新、浏览器缓存等：整理后仍会重新产生碎片。");
        sb.AppendLine("因此整卷 /A 不可能到 0%，本工具也不会给出“可以到 0”的承诺；");
        sb.AppendLine("能降低的是“可移动、可独占打开、非系统保护”的那部分文件碎片，是否收敛以 /A 前后对比为准。");
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
        var minFileKb = plan.Options.MinFileSizeKB > 0
            ? Math.Max(plan.Options.MinFileSizeKB, plan.Geometry.ClusterBytes / 1024)
            : 0;
        var minFileText = minFileKb > 0 ? $"{minFileKb}KB({plan.Options.MinFileClusters}簇)" : $"{plan.Options.MinFileClusters} 簇";
        sb.AppendLine($"安全余量、单批上限: 文件数<={plan.Options.MaxFiles} 字节数<={DiskService.FormatBytes(plan.Options.MaxBytes)}  " +
                      $"最小文件={minFileText}  " +
                      $"最小碎片率={plan.Options.MinFragmentationPercent:0.#}%  最小回收={DiskService.FormatBytes(plan.Options.MinReclaimBytes)}");
        sb.AppendLine($"整理质量参数: 校验={VerifyText(plan.Options.VerifyMode)}  " +
                      $"轮次上限={plan.Options.MaxRounds}  " +
                      $"总时长上限={(plan.Options.MaxRunMinutes <= 0 ? "不限" : $"{plan.Options.MaxRunMinutes} 分钟")}" +
                      (plan.Options.RelaxedFilters
                          ? "  （最后一轮为补扫轮：最小文件降至 1 簇，可回收下限临时放宽到 " +
                            $"{DiskService.FormatBytes(Math.Min(plan.Options.MinReclaimBytes <= 0 ? long.MaxValue : plan.Options.MinReclaimBytes, plan.Options.RelaxedMinReclaimBytes))}）"
                          : string.Empty));
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

    private static string FragText(ExternalFragResult? result)
    {
        if (result?.FragPercent is null)
            return $"未取得（exit={result?.ExitCode?.ToString() ?? "未执行"}, 错误码={result?.ErrorCode ?? "无"}）";
        return $"{result.FragPercent:0.##}%（exit={result.ExitCode?.ToString() ?? "未知"}）";
    }
}
