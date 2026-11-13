using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AutoDefrag.Rewrite;

namespace AutoDefrag;

/// <summary>
/// 后台执行引擎（在提权 worker 进程里运行）：
///   Clean  = 按介质选择参数：HDD 走 defrag &lt;盘&gt; /D [/X] /H（真正合并碎片），
///            SSD / 未知介质走 defrag &lt;盘&gt; /O（按介质优化，避免全盘整理损耗寿命）；
///            全程 CPU 守护（挂起/恢复），前后各探测一次碎片率；
///   Status = defrag &lt;盘&gt; /A 只读探测碎片率与剩余空间。
/// 参数取舍规则见 BuildPlan；/A 只用于探测，永远不与整理参数混用。
/// </summary>
public static class DefragEngine
{
    /// <summary>/X（空闲空间合并）的启用门槛：碎片率、空闲空间占比与空闲碎片度都必须达到该值。</summary>
    public const double MergeFreeSpaceFragThreshold = 5.0;
    public const double MergeFreeSpaceFreeRatio = 10.0;
    public const double MergeFreeSpaceFreeFragThreshold = 5.0;

    /// <summary>空闲空间少于 1GB 时不做 /X：搬移收益太小，耗时不划算。</summary>
    public const long MergeFreeSpaceMinFreeBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>defrag 输出里只保留这么多字符，避免日志被进度刷屏。</summary>
    private const int MaxCaptureChars = 3000;
    private const int MaxLogChars = 1500;

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    /// <summary>defrag 的 FACILITY_DEFRAG 错误码，例如权限不足的 0x89000024。</summary>
    private static readonly Regex ErrorCodeRegex = new(@"0x89[0-9a-fA-F]{6}", RegexOptions.Compiled);

    /// <summary>defrag 进度行里的百分比，例如“已完成 30%”“30% complete”。解析不到就不显示确定进度。</summary>
    private static readonly Regex ProgressPercentRegex = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    static DefragEngine()
    {
        // 控制台程序（defrag.exe）按 OEM 代码页输出，注册后才能正确解码中文报告
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }


    public static int RunClean(string[] drives, int cpuThreshold, string engineMode = "Self", int highSeconds = 10, int lowSeconds = 10,
        int minFragmentPercent = 0, DefragPreferences? settings = null)
    {
        var targets = FilterToFixed(drives);
        if (targets.Count == 0)
        {
            Logger.Error("未指定有效的本地固定磁盘，引擎退出");
            return 2;
        }

        var prefs = (settings ?? DefragPreferences.Default).Normalized();
        var qualityName = DefragQualityPresets.Match(prefs)?.Name
                          ?? (prefs.Quality == DefragQualityPresets.Custom ? "自定义" : DefragQualityPresets.Get(prefs.Quality).Name);

        // 上次可能残留停止标记（例如在 worker 完成后才创建的），开始时清掉，避免本次一启动就停
        ClearStopFlag();
        ProgressLog.Begin();

        var modeText = EngineResolver.ModeText(engineMode);
        Logger.Info($"================ 开始整理磁盘: 目标={string.Join(",", targets)} CPU上限={cpuThreshold}% " +
                    $"整理方式={modeText} 文件碎片率={(minFragmentPercent > 0 ? $"≥{minFragmentPercent}%" : "不限")} " +
                    $"整理质量={qualityName} 最小文件={prefs.MinFileSizeKB}KB 单轮≤{prefs.MaxFilesPerRun}文件/{prefs.MaxBytesPerRunGB}GB " +
                    $"可回收≥{prefs.MinReclaimMB}MB 校验={prefs.VerifyMode} 最多{prefs.MaxRounds}轮/{(prefs.MaxRunMinutes == 0 ? "不限时" : prefs.MaxRunMinutes + "分钟")} " +
                    $"固态只优化={(prefs.SsdOptimizeOnly ? "是" : "否")} " +
                    $"持续{highSeconds}s暂停/回落{lowSeconds}s继续 ================");
        ProgressLog.Append($"开始整理：目标={string.Join(",", targets)}；整理方式={modeText}；质量={qualityName}");

        var state = new WorkerStateFile
        {
            Mode = "clean",
            Stage = "准备中",
            DriveTotal = targets.Count,
            StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Message = "正在准备整理",
            Percent = -1
        };
        state.Save();

        var results = new List<DriveRunResult>();

        for (var index = 0; index < targets.Count; index++)
        {
            if (File.Exists(AppPaths.StopFlagFile))
            {
                Logger.Warn("检测到停止标记，整理在开始前终止");
                results.Add(new DriveRunResult(targets[index], DiskMediaKind.Unknown, "已停止", null, 0, 0,
                    DateTime.Now, DateTime.Now, "已停止", null, null, 0));
                break;
            }

            var drive = targets[index];
            state.Drive = drive;
            state.DriveLabel = drive.TrimEnd(':');
            state.DriveIndex = index + 1;
            state.Suspended = 0;
            state.Resumed = 0;
            state.Percent = -1;
            state.Phase = string.Empty;
            state.CurrentFile = string.Empty;
            state.FileIndex = 0;
            state.FileTotal = 0;
            state.ProcessedBytes = 0;
            state.TotalBytes = 0;
            state.SpeedBytesPerSec = 0;
            state.Round = 0;
            state.RoundTotal = 0;
            state.CoveragePercent = -1;
            state.Message = $"正在分析 {drive} 的碎片率";
            state.Stage = "分析碎片";
            state.Save();

            var volumeBytes = TotalBytesOf(drive);
            var info = FixedDriveByLetter(drive);
            var media = info is null ? DiskMediaKind.Unknown : DiskService.GetMediaKind(info);
            var freeBytes = info is null ? 0 : SafeFreeBytes(info);
            var clusterBytes = Native.GetClusterSize(drive);

            // 每块盘本轮实际采用的方式与判断依据（Auto 按存储块大小收敛）
            var effective = EngineResolver.Effective(engineMode, clusterBytes);
            var decision = EngineResolver.Describe(engineMode, clusterBytes);
            state.Engine = EngineResolver.ShortName(effective);
            state.EngineDetail = decision;
            Logger.Info($"{drive} 整理方式决策: {decision}（配置={modeText}，存储块={DiskService.ClusterText(clusterBytes)}）");
            ProgressLog.Append($"{drive} 整理方式: {decision}");

            // 大簇卷：系统 defrag 无法真正整理，按实际采用的整理方式分流
            if (DiskService.IsLargeCluster(clusterBytes))
            {
                if (effective == "System")
                {
                    Logger.Warn($"{drive} 的存储块为 {DiskService.ClusterText(clusterBytes)}（大于 4KB）：Windows 自带整理对该磁盘无效，" +
                                "本次跳过并不报成功");
                    ProgressLog.Append($"{drive} 跳过：{decision}");
                    results.Add(new DriveRunResult(drive, media, "系统方式对大容量存储块磁盘无效", null, 0, 0,
                        DateTime.Now, DateTime.Now, "系统方式无效（该磁盘）", null, null, clusterBytes));
                    continue;
                }

                // 固态硬盘上的大簇卷：默认不做文件级重写（避免无谓写入磨损），改为系统优化 /O。
                // 必须明确写日志/进度/结果说明，避免用户误以为“点了没反应”。
                if (prefs.SsdOptimizeOnly && media == DiskMediaKind.Ssd)
                {
                    var optimize = BuildPlan(drive, media, null, volumeBytes, freeBytes, clusterBytes);
                    Logger.Info($"{drive} 是固态硬盘且存储块为 {DiskService.ClusterText(clusterBytes)}：" +
                                "按设置跳过文件级重写（避免写入磨损），改为系统优化 " + optimize.CommandLine);
                    ProgressLog.Append($"{drive} 固态硬盘：不做文件级重写（避免写入磨损），改为系统优化 {optimize.Arguments}");
                    state.Engine = "系统优化";
                    state.EngineDetail = decision + "；固态硬盘默认跳过文件级重写";
                    state.Stage = $"正在{optimize.Verb}";
                    state.Message = $"固态硬盘：正在{optimize.Verb} {drive}（已跳过文件级重写）";
                    state.Save();

                    var optimizeRun = RunOneDrive(drive, optimize, null, cpuThreshold, highSeconds, lowSeconds, state);
                    results.Add(new DriveRunResult(
                        drive, media, optimize.Arguments, optimizeRun.ExitCode,
                        optimizeRun.Suspended, optimizeRun.Resumed,
                        optimizeRun.StartTime, optimizeRun.EndTime,
                        optimizeRun.Status, null, null, clusterBytes, null, 0));
                    continue;
                }

                if (media == DiskMediaKind.Ssd)
                {
                    Logger.Warn($"{drive} 是固态硬盘，但设置允许文件级重写（SsdOptimizeOnly=false），将执行重写；这会增加写入量");
                }
                else if (prefs.SsdOptimizeOnly && media == DiskMediaKind.Unknown)
                {
                    Logger.Warn($"{drive} 介质类型无法识别：保守起见仍按文件级重写处理（系统 /O 对该簇大小可能无效）");
                }

                Logger.Info($"{drive} 的存储块为 {DiskService.ClusterText(clusterBytes)}（大于 4KB）：不使用系统 defrag，改走文件级重写整理");
                if (DefragPolicy.IgnoresSystemNoAction(clusterBytes))
                {
                    // 策略：大簇卷上 /A 的“无需整理/0%”不能作为跳过依据；这里显式写明，
                    // 避免以后有人把 /A 结果接进分派条件时误伤唯一有效的整理路径。
                    Logger.Info($"{drive} 策略：簇 {clusterBytes} 字节 > {DefragPolicy.SystemDefragClusterLimit}，" +
                                "忽略系统 /A 的“无需整理”结论，始终执行文件级重写");
                    ProgressLog.Append($"{drive} 大簇卷：系统 /A 的“无需整理”不作为跳过依据，继续文件级重写");
                }
                var large = LargeClusterRunner.Run(drive, media, clusterBytes, state, dryRun: false,
                    scanRoot: null, cpuThreshold: cpuThreshold, highSeconds: highSeconds, lowSeconds: lowSeconds,
                    minFragmentPercent: minFragmentPercent, engineMode: engineMode, settings: prefs);
                results.Add(large);
                continue;
            }

            // 预检同样是重 I/O：纳入 CPU 守护，命中阈值就挂起 defrag /A（阶段=预检）
            var preProbe = Probe(drive, volumeBytes, cpuThreshold, highSeconds, lowSeconds, state, "预检");
            if (preProbe.Stopped)
            {
                Logger.Warn($"{drive} 的 /A 预检被用户停止，本次整理提前结束");
                results.Add(new DriveRunResult(drive, media, string.Empty, preProbe.ExitCode,
                    preProbe.Suspended, preProbe.Resumed, DateTime.Now, DateTime.Now, "已停止", null, null, clusterBytes));
                break;
            }
            if (preProbe.Suspended > 0 || preProbe.Resumed > 0)
            {
                Logger.Info($"{drive} /A 预检 CPU 守护: 暂停={preProbe.Suspended}次, 继续={preProbe.Resumed}次");
            }

            var preFrag = preProbe.FragPercent;

            // 机械盘空闲空间合并（/X）：只对机械盘、且碎片率/空闲占比/空闲碎片度都达标时才追加。
            // 门槛不满足时只写原因，不跑长时间的空闲搬移。
            VolumeLayout? freeLayout = media == DiskMediaKind.Hdd
                ? VolumeAnalyzer.ReadVolumeLayout(drive, RewriteOptions.Default)
                : null;
            var mergeGate = EvaluateMergeGate(prefs.MergeFreeSpace, media, preFrag, volumeBytes, freeBytes, freeLayout);
            if (media == DiskMediaKind.Hdd)
            {
                Logger.Info($"{drive} 空闲空间合并评估: {(mergeGate.Allowed ? "启用" : "不启用")}；{mergeGate.Reason}");
                ProgressLog.Append($"{drive} 空闲空间合并: {(mergeGate.Allowed ? "启用 /X" : "不启用")}（{mergeGate.Reason}）");
            }

            var plan = BuildPlan(drive, media, preFrag, volumeBytes, freeBytes, clusterBytes, mergeGate);
            Logger.Info($"计划 {drive}: 类型={DiskService.MediaText(media)} 存储块={DiskService.ClusterText(clusterBytes)} " +
                        $"整理方式={decision}（小存储块仍用系统 defrag） 命令={plan.CommandLine} 原因={plan.Reason}");
            ProgressLog.Append($"{drive} 计划: {plan.CommandLine}；{decision}");

            state.TotalBytes = 0;
            state.Percent = -1;
            state.Phase = string.Empty;
            state.Stage = $"正在{plan.Verb}";
            state.Message = $"正在{plan.Verb} {drive}（{DiskService.MediaText(media)}，第 {index + 1}/{targets.Count} 个磁盘）";
            state.Save();

            // 预检已经发生的挂起/恢复作为基数带入整理阶段，worker_state.json 的口径始终是“本盘累计”
            var run = RunOneDrive(drive, plan, preFrag, cpuThreshold, highSeconds, lowSeconds, state,
                preProbe.Suspended, preProbe.Resumed);

            // /D /X 合并执行失败时回退为只 /D：空闲合并失败不能连带把文件整理也记成成功或放弃
            string mergeStatus = plan.MergeRequested
                ? (run.Status == "成功" ? "成功" : run.Status)
                : "未启用";
            string mergeDetail = plan.Reason;
            if (plan.MergeRequested && run.Status != "成功")
            {
                var fallback = BuildPlan(drive, media, preFrag, volumeBytes, freeBytes, clusterBytes,
                    new MergeGate(false, "合并空闲空间失败，回退为只整理碎片", preFrag, 0, null));
                Logger.Warn($"{drive} 合并空闲空间未成功（{run.Status}, exit={FormatExit(run.ExitCode)}, 错误码={run.ErrorCode ?? "无"}），" +
                            $"回退为 {fallback.CommandLine}");
                ProgressLog.Append($"{drive} 合并空闲空间未成功（{run.Status}），回退为只整理碎片");
                var fallbackRun = RunOneDrive(drive, fallback, preFrag, cpuThreshold, highSeconds, lowSeconds, state,
                    run.Suspended, run.Resumed);
                mergeDetail = plan.Reason + $"；/X 未成功：{run.Status}（exit={FormatExit(run.ExitCode)}, 错误码={run.ErrorCode ?? "无"}）";
                fallbackRun = fallbackRun with
                {
                    Suspended = fallbackRun.Suspended + run.Suspended,
                    Resumed = fallbackRun.Resumed + run.Resumed
                };
                run = fallbackRun.Status == "成功"
                    ? fallbackRun with { Status = "部分成功", MergeStatus = mergeStatus, MergeDetail = mergeDetail }
                    : fallbackRun with { MergeStatus = mergeStatus, MergeDetail = mergeDetail };
            }

            state.Stage = "分析碎片";
            state.Message = $"正在复核 {drive} 的碎片率";
            state.Percent = -1;
            state.Phase = string.Empty;
            state.CurrentFile = string.Empty;
            state.SpeedBytesPerSec = 0;
            state.Save();
            // 复核同样跑 /A：继续沿用同一套守护，并把已累计的挂起/恢复次数作为基数
            var postProbe = Probe(drive, volumeBytes, cpuThreshold, highSeconds, lowSeconds, state, "复核",
                run.Suspended + preProbe.Suspended, run.Resumed + preProbe.Resumed);
            if (postProbe.Suspended > 0 || postProbe.Resumed > 0)
            {
                Logger.Info($"{drive} /A 复核 CPU 守护: 暂停={postProbe.Suspended}次, 继续={postProbe.Resumed}次");
            }

            results.Add(new DriveRunResult(
                drive, media, plan.Arguments, run.ExitCode,
                run.Suspended + preProbe.Suspended + postProbe.Suspended,
                run.Resumed + preProbe.Resumed + postProbe.Resumed,
                run.StartTime, run.EndTime,
                postProbe.Stopped ? "已停止" : run.Status, preFrag, postProbe.FragPercent, clusterBytes,
                MergeStatus: mergeStatus, MergeDetail: mergeDetail,
                WinFragBefore: preFrag, WinFragAfter: postProbe.FragPercent));
        }

        var okCount = results.Count(r => r.Status == "成功");
        var total = results.Count;
        var summary = string.Join("; ", results.Select(r =>
            $"{r.Drive}[{DiskService.MediaText(r.Media)} 存储块={DiskService.ClusterText(r.ClusterBytes)} {r.Arguments} " +
            $"方式={EngineResolver.Describe(engineMode, r.ClusterBytes)}] " +
            $"状态={r.Status},exit={r.ExitCode?.ToString() ?? "--"},暂停={r.Suspended},继续={r.Resumed}," +
            $"碎片{FormatFrag(r.PreFrag)}->{FormatFrag(r.PostFrag)}" +
            (r.CoveragePercent is null ? string.Empty : $",覆盖率={r.CoveragePercent:0.#}%（仅已分析文件）") +
            (r.Rounds > 0 ? $",轮次={r.Rounds}" : string.Empty) +
            (r.WinFragBefore is null && r.WinFragAfter is null ? string.Empty :
                $",Windows/A={FormatFrag(r.WinFragBefore)}->{FormatFrag(r.WinFragAfter)}") +
            (r.MergeStatus is "未启用" or "" ? string.Empty : $",空闲合并={r.MergeStatus}") +
            (string.IsNullOrEmpty(r.ErrorCode) ? string.Empty : $",错误码={r.ErrorCode}")));

        Logger.Info($"全部完成: 成功={okCount}/{total}  累计暂停={results.Sum(r => r.Suspended)}次 继续={results.Sum(r => r.Resumed)}次   明细: {summary}");
        UpdateDriveHistory(results);
        WriteLastResult(string.Join(",", targets), summary, okCount, total);

        // 与旧脚本一致：Clean 跑完后写入"每日一次"锁；
        // 被用户中途停止时不写锁，让当天还能再整理一次
        var anyStopped = results.Any(r => r.Status == "已停止");
        if (anyStopped)
        {
            Logger.Warn("本次整理被用户停止，不写入每日锁（当天仍可再次触发）");
        }
        else
        {
            WriteLastRunDate();
        }

        ClearStopFlag();

        state.Stage = "完成";
        state.Drive = string.Empty;
        state.DriveLabel = string.Empty;
        state.Percent = -1;
        state.Phase = string.Empty;
        state.CurrentFile = string.Empty;
        state.Message = anyStopped
            ? $"已停止（成功 {okCount}/{total}）"
            : okCount == total
                ? $"已完成 {total} 个磁盘"
                : $"结束：成功 {okCount}/{total}";
        state.Save();
        ProgressLog.Append(anyStopped ? $"整理已停止：成功 {okCount}/{total}" : $"整理结束：成功 {okCount}/{total}");

        return anyStopped ? 4 : (okCount == total ? 0 : 1);
    }

    public static int RunStatus(string[] drives, int cpuThreshold = 70, int highSeconds = 10, int lowSeconds = 10)
    {
        var targets = drives.Length == 0
            ? FilterToFixed(Array.Empty<string>(), allowAllWhenEmpty: true)
            : FilterToFixed(drives);
        if (targets.Count == 0)
        {
            Logger.Error("没有可检查的磁盘，程序退出");
            return 2;
        }

        Logger.Info($"================ 开始检查磁盘碎片: 目标={string.Join(",", targets)} ================");
        // 每次新检查都从干净状态开始；上次异常退出残留的停止标记不应中断本次检查
        ClearStopFlag();
        ProgressLog.Begin();
        ProgressLog.Append($"开始检查碎片：目标={string.Join(",", targets)}");

        var state = new WorkerStateFile
        {
            Mode = "status",
            Stage = "检查磁盘",
            DriveTotal = targets.Count,
            StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Message = "正在读取碎片情况",
            Percent = 0
        };
        state.Save();

        var file = new DriveStatusFile { ProbeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
        // 检查磁盘只刷新探测值；最近一次整理历史（LastRunAt/Status/ErrorCode）从旧文件原样带过来
        var previousStatus = DriveStatusFile.TryLoad();
        var stopped = false;
        for (var index = 0; index < targets.Count; index++)
        {
            var drive = targets[index];
            state.Drive = drive;
            state.DriveIndex = index + 1;
            state.DriveTotal = targets.Count;
            state.DriveLabel = drive.TrimEnd(':');
            state.Percent = (int)(index * 100.0 / targets.Count);
            state.Message = $"正在检查 {drive}（{index + 1}/{targets.Count}）";
            state.Save();

            var info = FixedDriveByLetter(drive);
            var volumeBytes = info is null ? 0 : DiskService.TotalBytes(info);
            var clusterBytes = Native.GetClusterSize(drive);

            double? frag;
            bool probed;
            if (DiskService.IsLargeCluster(clusterBytes))
            {
                // 大簇卷：defrag /A 会给出不可信的 0%，改用位图 + Runlist 自算
                var analysis = VolumeAnalyzer.Analyze(drive, RewriteOptions.Quick);
                frag = analysis?.FileFragmentationPercent;
                probed = analysis is not null;
                Logger.Info($"{drive} 大簇卷自算碎片率: {FormatFrag(frag)}（系统 defrag /A 在该簇大小下不可信，不再采用）");
            }
            else
            {
                var probe = Probe(drive, volumeBytes, cpuThreshold, highSeconds, lowSeconds, state, "预检");
                frag = probe.FragPercent;
                probed = probe.FragPercent is not null;

                if (probe.Stopped)
                {
                    Logger.Warn($"{drive} 的 /A 预检被用户停止，碎片检查提前结束");
                    stopped = true;
                    break;
                }

                // 一致性对比：同一卷上用自算口径再算一次，差异过大时写 WARN（两者统计对象不同，不要求相等）
                var selfCheck = VolumeAnalyzer.Analyze(drive, RewriteOptions.Compare);
                if (selfCheck is not null && frag is not null)
                {
                    var diff = Math.Abs(selfCheck.FileFragmentationPercent - frag.Value);
                    var line = $"{drive} 碎片率口径对比: 系统 defrag /A={frag:0.##}%（按碎片文件空间/卷容量），" +
                               $"自算={selfCheck.FileFragmentationPercent:0.##}%（按非最大区段簇数/已分析文件簇数），差异={diff:0.##} 个百分点";
                    if (diff >= 10) Logger.Warn(line + "；两者分母不同，仅作参考");
                    else Logger.Info(line);
                }
            }

            var previousEntry = previousStatus?.Drives.FirstOrDefault(d =>
                string.Equals(d.Drive, drive, StringComparison.OrdinalIgnoreCase));
            file.Drives.Add(new DriveStatusEntry
            {
                Drive = drive,
                VolumeSizeGB = volumeBytes > 0 ? Math.Round(volumeBytes / 1024.0 / 1024 / 1024, 2) : null,
                FreeSpaceGB = info is null ? null : Math.Round(SafeFreeBytes(info) / 1024.0 / 1024 / 1024, 2),
                FragPercent = frag,
                Probed = probed,
                LastRunAt = previousEntry?.LastRunAt ?? string.Empty,
                LastRunStatus = previousEntry?.LastRunStatus ?? string.Empty,
                LastErrorCode = previousEntry?.LastErrorCode ?? string.Empty
            });
        }

        file.Save();

        var summary = string.Join("; ", file.Drives.Select(d =>
            $"{d.Drive}(碎片={FormatFrag(d.FragPercent)},剩余={d.FreeSpaceGB?.ToString() ?? "--"}GB/共={d.VolumeSizeGB?.ToString() ?? "--"}GB,读取={(d.Probed ? "成功" : "失败")})"));
        Logger.Info("磁盘检查完成: " + summary);
        // 检查磁盘只更新 drive_status.json，绝不覆盖 last_result.txt（那是整理结果）

        state.Stage = "完成";
        state.Percent = -1;
        state.Message = stopped ? "碎片检查被用户停止" : "碎片情况检查完成";
        state.Save();
        ProgressLog.Append(stopped ? "碎片检查已停止" : "碎片检查完成");

        // 停止标记只对“本次检查”有效；检查结束即清理，避免残留影响下一次整理
        ClearStopFlag();
        return stopped ? 4 : 0;
    }

    /// <summary>
    /// 选择 defrag 参数（冲突取舍规则）：
    ///  1. /A 只用于只读探测，永远不与任何整理参数组合；
    ///  2. 介质决定基础操作：HDD 用 /D（传统碎片整理，真正合并碎片）；
    ///     SSD 与无法识别的介质用 /O（按介质优化，SSD 上是 retrim/优化，不做全盘整理）；
    ///     /O 与 /D、/X 互斥，按介质优先，绝不并存；
    ///  3. /X（空闲空间合并）只在 HDD 且必须与 /D 搭配；仅当碎片率 ≥ 5%、空闲空间 ≥ 10% 且 ≥1GB、
    ///     空闲碎片度 ≥ 5% 时追加，否则只跑 /D，避免无意义的长时间空闲空间搬移；
    ///  4. /H（普通优先级）只对 HDD 有意义：HDD 固定追加，SSD 的 /O 不加；
    ///  5. 介质无法识别时按 SSD 保守处理（只 /O），避免误对固态盘做全盘整理。
    /// </summary>
    private static DefragPlan BuildPlan(
        string drive, DiskMediaKind media, double? preFrag, long volumeBytes, long freeBytes, int clusterBytes,
        MergeGate? mergeGate = null)
    {
        var largeCluster = DiskService.IsLargeCluster(clusterBytes);
        var prefix = largeCluster
            ? $"【可能不支持：存储块 {DiskService.ClusterText(clusterBytes)} 大于 4KB】"
            : string.Empty;

        if (media == DiskMediaKind.Hdd)
        {
            var gate = mergeGate ?? EvaluateMergeGate(true, media, preFrag, volumeBytes, freeBytes, null);
            var reason = gate.Reason;
            return new DefragPlan(drive, media, gate.Allowed ? "/D /X /H" : "/D /H",
                "整理", prefix + reason, clusterBytes);
        }

        var reasonSsd = media == DiskMediaKind.Ssd
            ? "固态硬盘：只做优化（/O，含 TRIM），不做全盘整理，避免额外写入影响寿命"
            : "无法识别磁盘类型，按固态硬盘保守处理：只做优化（/O），不做全盘整理";
        return new DefragPlan(drive, media, "/O", "优化", prefix + reasonSsd, clusterBytes);
    }

    /// <summary>
    /// 机械盘空闲空间合并（/X）的门槛评估。只有同时满足以下条件才执行：
    ///   1. 设置开关打开；
    ///   2. 介质明确是机械硬盘（固态/未知介质绝不执行）；
    ///   3. 碎片率 ≥ 5%；
    ///   4. 空闲空间 ≥ 10% 且 ≥ 1GB；
    ///   5. 读到卷位图时，空闲碎片度 ≥ 5%（空闲空间已经很连续时不做无谓搬移）。
    /// 返回文本同时用于日志、进度和报告，避免“为什么没做”无从解释。
    /// </summary>
    public static MergeGate EvaluateMergeGate(
        bool enabled, DiskMediaKind media, double? fragPercent,
        long volumeBytes, long freeBytes, VolumeLayout? freeLayout)
    {
        var freeRatio = volumeBytes > 0 ? freeBytes * 100.0 / volumeBytes : 0.0;
        var freeFrag = freeLayout?.FreeFragmentationPercent;

        if (!enabled)
            return new MergeGate(false, "机械硬盘空闲空间合并已关闭（设置里可重新开启）", fragPercent, freeRatio, freeFrag);
        if (media != DiskMediaKind.Hdd)
            return new MergeGate(false, $"仅机械硬盘执行空闲空间合并（当前介质：{DiskService.MediaText(media)}）", fragPercent, freeRatio, freeFrag);
        if (fragPercent is null)
            return new MergeGate(false, "读取不到碎片率，无法确认是否需要合并空闲空间，本次只整理碎片", fragPercent, freeRatio, freeFrag);
        if (fragPercent.Value < MergeFreeSpaceFragThreshold)
            return new MergeGate(false,
                $"碎片率 {fragPercent:0.##}% 低于合并门槛 {MergeFreeSpaceFragThreshold:0}%，本次只整理碎片（/D）",
                fragPercent, freeRatio, freeFrag);
        if (freeBytes < MergeFreeSpaceMinFreeBytes)
            return new MergeGate(false,
                $"空闲空间 {DiskService.FormatBytes(freeBytes)} 少于 {DiskService.FormatBytes(MergeFreeSpaceMinFreeBytes)}，合并收益太小，本次只整理碎片",
                fragPercent, freeRatio, freeFrag);
        if (freeRatio < MergeFreeSpaceFreeRatio)
            return new MergeGate(false,
                $"空闲空间占比 {freeRatio:0.#}% 低于合并门槛 {MergeFreeSpaceFreeRatio:0}%，本次只整理碎片",
                fragPercent, freeRatio, freeFrag);
        if (freeFrag is not null && freeFrag.Value < MergeFreeSpaceFreeFragThreshold)
            return new MergeGate(false,
                $"空闲碎片度 {freeFrag:0.##}% 低于合并门槛 {MergeFreeSpaceFreeFragThreshold:0}%（空闲空间已足够连续），本次只整理碎片",
                fragPercent, freeRatio, freeFrag);

        var freeFragText = freeFrag is null ? "空闲碎片度未知" : $"空闲碎片度 {freeFrag:0.##}%";
        return new MergeGate(true,
            $"机械硬盘：碎片 {fragPercent:0.##}% ≥ {MergeFreeSpaceFragThreshold:0}%、空闲 {freeRatio:0.#}% ≥ {MergeFreeSpaceFreeRatio:0}%、{freeFragText}，" +
            $"本次先合并空闲空间（/X）再整理碎片（/D）",
            fragPercent, freeRatio, freeFrag);
    }

    private static DriveRunResult RunOneDrive(
        string drive, DefragPlan plan, double? preFrag, int cpuThreshold, int highSeconds, int lowSeconds,
        WorkerStateFile state, int suspendedBase = 0, int resumedBase = 0)
    {
        var startTime = DateTime.Now;
        var suspended = 0;
        var resumed = 0;
        var capture = new OutputCapture();

        Logger.Info($"开始{plan.Verb} {drive}   命令=defrag.exe {plan.CommandLine}   CPU阈值={cpuThreshold}%");

        System.Diagnostics.Process? process;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "defrag.exe",
                Arguments = plan.CommandLine,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };
            process = System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Error($"启动 defrag.exe 失败({drive}): 命令=defrag.exe {plan.CommandLine}  {ex.Message}");
            return new DriveRunResult(drive, plan.Media, plan.Arguments, null, 0, 0, startTime, DateTime.Now, "启动失败", null, null);
        }

        if (process is null)
        {
            Logger.Error($"启动 defrag.exe 失败({drive}): 命令=defrag.exe {plan.CommandLine}  进程对象为空");
            return new DriveRunResult(drive, plan.Media, plan.Arguments, null, 0, 0, startTime, DateTime.Now, "启动失败", null, null);
        }

        // 进程对象要在返回前释放；否则每次整理都会留下一个句柄/内核对象
        using var processLifetime = process;

        var lastLoggedPercent = -1;
        void CaptureLine(string? line)
        {
            capture.Append(line);

            // 尽量从 defrag 输出里取真实百分比；取不到就保持 Percent=-1（界面显示不确定态）
            var percent = ParseProgressPercent(line);
            if (percent is null) return;

            var value = percent.Value;
            state.Percent = value;
            if (value >= lastLoggedPercent + 5 || value == 100)
            {
                lastLoggedPercent = value;
                ProgressLog.Append($"{drive} {plan.Verb}进度 {value}%");
            }
        }

        // 必须持续排空输出，否则 defrag 的进度输出可能填满管道缓冲区导致进程卡死
        process.OutputDataReceived += (_, e) => CaptureLine(e.Data);
        process.ErrorDataReceived += (_, e) => CaptureLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Logger.Info($"defrag.exe 已启动, PID={process.Id}");

        var sampler = new SystemCpuSampler();
        sampler.Sample();          // 预热：建立首个采样基准
        Thread.Sleep(1000);

        var phase = "Running";     // Running / Suspended
        var highCount = 0;
        var lowCount = 0;
        var stopped = false;

        state.Suspended = suspendedBase;
        state.Resumed = resumedBase;
        state.Message = $"正在{plan.Verb} {drive}";
        state.Save();

        while (!HasExited(process))
        {
            Thread.Sleep(1000);
            if (HasExited(process)) break;

            // 用户点“停止整理”：结束 defrag 进程，已完成的整理效果保留（系统整理本身是分段提交的）
            if (File.Exists(AppPaths.StopFlagFile))
            {
                Logger.Warn($"检测到停止标记：正在结束 {drive} 的整理进程");
                if (phase == "Suspended")
                {
                    Native.TryResume(process.Id, out _);
                }
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Logger.Warn("结束整理进程失败: " + ex.Message); }
                stopped = true;
                break;
            }

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
                            state.Suspended = suspendedBase + suspended;
                            state.Message = $"CPU 过高，已暂停 {drive} 的{plan.Verb}";
                            Logger.Warn($"CPU 连续 > {cpuThreshold}% 达 {highSeconds}s  ->  暂停整理程序 (PID={process.Id}, 当前CPU={cpu:N1}%)");
                        }
                        else
                        {
                            Logger.Warn($"暂停整理程序失败: {error}");
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
                            state.Resumed = resumedBase + resumed;
                            state.Message = $"正在{plan.Verb} {drive}";
                            Logger.Info($"CPU 回落并稳定 {lowSeconds}s  ->  继续整理程序 (PID={process.Id}, 当前CPU={cpu:N1}%)");
                        }
                        else
                        {
                            Logger.Warn($"继续整理程序失败: {error}");
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

        var endTime = DateTime.Now;
        var elapsed = endTime - startTime;
        var output = capture.Text;
        var errorCode = FindErrorCode(output);
        var status = stopped ? "已停止" : ClassifyResult(plan, preFrag, exitCode, errorCode, output, elapsed);
        if (errorCode is not null)
        {
            Logger.Warn($"{drive} {plan.Verb}错误码解释: {errorCode} -> {DefragResultCodes.Describe(errorCode)}");
        }

        Logger.Info($"{drive} {plan.Verb}结束: 命令=defrag.exe {plan.CommandLine}, exit={FormatExit(exitCode)}, 状态={status}, 暂停={suspended}次, 继续={resumed}次, 用时={elapsed.TotalSeconds:N0}s");
        if (errorCode is not null)
        {
            Logger.Warn($"{drive} 整理程序输出包含错误码 {errorCode}，按失败处理");
        }
        Logger.Info($"{drive} 整理程序输出片段: {(output.Length == 0 ? "(无输出)" : ClipForLog(output))}");
        ProgressLog.Append($"{drive} {plan.Verb}结束: 状态={status}, exit={FormatExit(exitCode)}, 用时={elapsed.TotalSeconds:N0}s");

        return new DriveRunResult(drive, plan.Media, plan.Arguments, exitCode, suspended, resumed, startTime, endTime, status, null, null, plan.ClusterBytes,
            ErrorCode: errorCode);
    }

    /// <summary>
    /// 大簇卷文件级整理前的空闲空间合并（/X）。
    /// 只接受机械硬盘调用；与文件重写共用同一套 CPU 守护、停止标记、进度与日志通道。
    /// 顺序上先 /X 再重写：/X 会把数据搬进更靠前的连续空闲区，为后续文件重写留下大段连续空间，
    /// 若反过来先重写、再 /X，重写产生的临时文件与备份会把空闲空间重新打散，合并效果被抵消。
    /// </summary>
    public static MergeRunResult RunFreeSpaceMerge(
        string drive, int cpuThreshold, int highSeconds, int lowSeconds, WorkerStateFile state)
    {
        var clusterBytes = Native.GetClusterSize(drive);
        var plan = new DefragPlan(drive, DiskMediaKind.Hdd, "/X /H", "合并空闲空间",
            "机械硬盘空闲空间合并（/X）；只移动文件位置，不改文件内容", clusterBytes);

        Logger.Info($"{drive} 开始合并空闲空间: defrag.exe {plan.CommandLine}（CPU 守护 {cpuThreshold}%）");
        ProgressLog.Append($"{drive} 开始合并空闲空间（/X）：CPU > {cpuThreshold}% 连续 {highSeconds}s 暂停，回落稳定 {lowSeconds}s 继续");
        state.Stage = "合并空闲空间";
        state.Phase = string.Empty;
        state.Message = $"正在合并 {drive} 的空闲空间（/X）";
        state.Percent = -1;
        state.Save();

        var run = RunOneDrive(drive, plan, null, cpuThreshold, highSeconds, lowSeconds, state);
        var status = run.Status switch
        {
            "成功" => "成功",
            "已停止" => "已停止",
            "可能不支持" => "可能不支持",
            "无法确认" => "无法确认",
            _ => "失败"
        };

        var detail = status switch
        {
            "成功" => "空闲空间合并完成",
            "失败" => $"空闲空间合并失败（exit={FormatExit(run.ExitCode)}, 错误码={run.ErrorCode ?? "无"}）",
            "已停止" => "空闲空间合并被用户停止",
            "可能不支持" => "系统未报告实际搬移证据，可能不支持该卷的 /X；本次结果不按成功计",
            _ => "系统报告成功但没有可确认的搬移证据"
        };

        Logger.Info($"{drive} 合并空闲空间结束: 状态={status}, exit={FormatExit(run.ExitCode)}, 错误码={run.ErrorCode ?? "无"}, " +
                    $"暂停={run.Suspended}次, 继续={run.Resumed}次, 用时={(run.EndTime - run.StartTime).TotalSeconds:N0}s；{detail}");
        ProgressLog.Append($"{drive} 合并空闲空间结束：{status}（exit={FormatExit(run.ExitCode)}）");

        return new MergeRunResult(status, run.ExitCode, run.ErrorCode, run.Suspended, run.Resumed,
            run.StartTime, run.EndTime, detail);
    }

    /// <summary>
    /// 对外口径：执行只读 defrag &lt;drive&gt; /A 并返回整卷碎片率。
    /// 与整理共用 CPU 守护与停止标记；/A 失败时返回退出码/错误码，绝不编造数值。
    /// </summary>
    public static ExternalFragResult AnalyzeFrag(
        string drive, long volumeBytes, int cpuThreshold, int highSeconds, int lowSeconds,
        WorkerStateFile state, string stage)
    {
        var probe = Probe(drive, volumeBytes, cpuThreshold, highSeconds, lowSeconds, state, stage);
        return new ExternalFragResult(probe.FragPercent, probe.ExitCode, FindErrorCode(probe.Output),
            probe.Stopped, probe.Output);
    }

    private static bool HasExited(System.Diagnostics.Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    /// <summary>
    /// 执行只读 defrag &lt;drive&gt; /A 并解析碎片率。
    /// /A 也是重 I/O，因此与整理阶段同一套 CPU 守护：整机 CPU 每秒采样，
    /// 连续 highSeconds 秒高于阈值就挂起这个 defrag 进程，回落到阈值以下并稳定 lowSeconds 秒后恢复；
    /// 用户停止时先恢复再终止，避免留下挂起进程。
    /// 解析失败时把退出码与输出片段写进 engine.log，便于区分权限不足、超大簇限制或输出格式变化。
    /// </summary>
    private static ProbeResult Probe(
        string drive, long volumeBytes, int cpuThreshold, int highSeconds, int lowSeconds,
        WorkerStateFile state, string stage, int suspendedBase = 0, int resumedBase = 0)
    {
        var capture = new OutputCapture();
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
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                StandardOutputEncoding = ConsoleEncoding,
                StandardErrorEncoding = ConsoleEncoding
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                Logger.Warn($"defrag /A 无法启动({drive})");
                return new ProbeResult(null, null, string.Empty);
            }

            process.OutputDataReceived += (_, e) => capture.Append(e.Data);
            process.ErrorDataReceived += (_, e) => capture.Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var guard = MonitorProbe(process, drive, stage, cpuThreshold, highSeconds, lowSeconds,
                state, suspendedBase, resumedBase);

            int? exitCode = null;
            try { exitCode = process.ExitCode; } catch { }

            var output = capture.Text;
            var parsed = DefragOutputParser.Parse(output, volumeBytes);
            var frag = parsed.Percent;
            var errorCode = FindErrorCode(output);

            if (frag is null)
            {
                // 解析失败只降级为“未知”：不推断成 0%、不判定“无需整理”、也不影响退出码判断
                Logger.Warn($"读取碎片情况失败({drive}): exit={FormatExit(exitCode)}, 错误码={errorCode ?? "无"}, " +
                            $"解析={parsed.Method}，按“未知”处理（不据此跳过或判定成功），输出片段=\"{ClipForLog(output)}\"");
            }
            else if (errorCode is not null)
            {
                Logger.Warn($"读取碎片情况时输出包含错误码 {errorCode}({drive}, {DefragResultCodes.Describe(errorCode)}): " +
                            $"exit={FormatExit(exitCode)}，但碎片仍解析为 {frag:0.##}%（{parsed.Method}）");
            }

            return new ProbeResult(frag, exitCode, output, guard.Suspended, guard.Resumed, guard.Stopped);
        }
        catch (Exception ex)
        {
            Logger.Warn($"defrag /A 执行失败({drive}): {ex.Message}");
            return new ProbeResult(null, null, capture.Text);
        }
    }

    /// <summary>
    /// /A 预检的 CPU 守护循环：语义与整理阶段一致（整机 CPU 每秒采样，
    /// 连续 highSeconds 秒高于阈值就挂起 defrag 进程，回落到阈值以下并稳定 lowSeconds 秒后恢复）。
    /// 返回本次预检自己的挂起/恢复次数；计数同时写入 worker_state.json。
    /// </summary>
    private static ProbeGuardOutcome MonitorProbe(
        System.Diagnostics.Process process, string drive, string stage,
        int cpuThreshold, int highSeconds, int lowSeconds, WorkerStateFile state,
        int suspendedBase, int resumedBase)
    {
        var sampler = new SystemCpuSampler();
        sampler.Sample();          // 预热：建立首个采样基准
        Thread.Sleep(1000);

        var suspended = 0;
        var resumed = 0;
        var phase = "Running";     // Running / Suspended
        var highCount = 0;
        var lowCount = 0;
        var stopped = false;

        state.Phase = stage;
        state.Message = $"正在{stage} {drive} 的碎片率（/A）";
        state.Suspended = suspendedBase;
        state.Resumed = resumedBase;
        state.Save();

        while (!HasExited(process))
        {
            Thread.Sleep(1000);
            if (HasExited(process)) break;

            // 用户点“停止整理”：被挂起的预检进程必须先恢复再结束，避免留下挂起进程
            if (File.Exists(AppPaths.StopFlagFile))
            {
                Logger.Warn($"检测到停止标记：正在结束 {drive} 的 /A {stage}进程");
                if (phase == "Suspended")
                {
                    Native.TryResume(process.Id, out _);
                }
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) { Logger.Warn("结束预检进程失败: " + ex.Message); }
                stopped = true;
                break;
            }

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
                            state.Suspended = suspendedBase + suspended;
                            state.Message = $"CPU 过高，已暂停 {drive} 的{stage}（/A）";
                            Logger.Warn($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CPU 连续 > {cpuThreshold}% 达 {highSeconds}s -> " +
                                        $"暂停 defrag {drive} /A（阶段={stage}, PID={process.Id}, 实测={cpu:N1}%, 阈值={cpuThreshold}%, 第 {suspended} 次）");
                            ProgressLog.Append($"{drive} CPU {cpu:N1}% > {cpuThreshold}%（持续 {highSeconds}s），已暂停 /A {stage}（第 {suspended} 次）");
                        }
                        else
                        {
                            Logger.Warn($"暂停 /A {stage}进程失败: {error}");
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
                            state.Resumed = resumedBase + resumed;
                            state.Message = $"正在{stage} {drive} 的碎片率（/A）";
                            Logger.Info($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CPU 回落并稳定 {lowSeconds}s（实测={cpu:N1}% ≤ 阈值={cpuThreshold}%）-> " +
                                        $"继续 defrag {drive} /A（阶段={stage}, PID={process.Id}, 第 {resumed} 次）");
                            ProgressLog.Append($"{drive} CPU 回落到 {cpu:N1}%，继续 /A {stage}（第 {resumed} 次）");
                        }
                        else
                        {
                            Logger.Warn($"继续 /A {stage}进程失败: {error}");
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

        state.Phase = string.Empty;
        state.Save();

        return new ProbeGuardOutcome(suspended, resumed, stopped);
    }

    /// <summary>按输出证据与介质判定本次整理结果，不只依赖退出码。</summary>
    private static string ClassifyResult(
        DefragPlan plan, double? preFrag, int? exitCode, string? errorCode, string output, TimeSpan elapsed)
    {
        if (errorCode is not null) return "失败";
        if (exitCode != 0) return "失败";

        var hasEvidence = HasOperationEvidence(output, elapsed);

        // 超大簇卷：看不到实际整理过程时，如实标为“可能不支持”，不报成功
        if (!hasEvidence && DiskService.IsLargeCluster(plan.ClusterBytes)) return "可能不支持";

        // 机械硬盘读不到碎片情况、且看不到实际整理过程 -> 无法确认
        if (!hasEvidence && plan.Media == DiskMediaKind.Hdd && preFrag is null) return "无法确认";

        return "成功";
    }

    /// <summary>
    /// 是否有“defrag 确实干活了”的旁证：有输出且运行时间明显超过启停开销。
    /// 真实整理/优化不可能在几秒内完成；1 秒左右且无实际输出的情况一律不认成功。
    /// </summary>
    private static bool HasOperationEvidence(string output, TimeSpan elapsed) =>
        output.Length > 0 && elapsed.TotalSeconds >= 5;

    private static string? FindErrorCode(string output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var match = ErrorCodeRegex.Match(output);
        return match.Success ? match.Value : null;
    }

    /// <summary>从 defrag 的输出行里提取进度百分比；没有或越界返回 null。</summary>
    private static int? ParseProgressPercent(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var match = ProgressPercentRegex.Match(line);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return null;
        return value is >= 0 and <= 100 ? value : null;
    }

    private static string FormatExit(int? exitCode) => exitCode?.ToString() ?? "未知";

    private static string ClipForLog(string output)
    {
        var text = (output ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        return text.Length <= MaxLogChars ? text : text[..MaxLogChars] + " …";
    }

    private static Encoding ConsoleEncoding
    {
        get
        {
            try
            {
                var page = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                return page > 0 ? Encoding.GetEncoding(page) : Encoding.UTF8;
            }
            catch
            {
                return Encoding.UTF8;
            }
        }
    }

    /// <summary>
    /// 解析 defrag 报告中的整卷碎片率。
    /// 具体规则在 DefragOutputParser：优先匹配“碎片空间”类键名，键名不认识时只在
    /// 全报告唯一一个百分比时采用；出现歧义或解析失败返回 null（未知），
    /// 调用方不得据此判定成功或“无需整理”。
    /// </summary>
    public static double? ParseFragPercent(string report, long volumeBytes) =>
        DefragOutputParser.Parse(report, volumeBytes).Percent;

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
                if (letter.Length > 0 && DiskService.IsValidDriveSpec(letter)) requested.Add(letter);
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

    /// <summary>开始新任务前清掉上次遗留的停止标记。</summary>
    private static void ClearStopFlag()
    {
        try
        {
            if (File.Exists(AppPaths.StopFlagFile))
            {
                File.Delete(AppPaths.StopFlagFile);
                Logger.Info("已清理上次遗留的停止标记");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("清理停止标记失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 把本次真实整理的结果回填到 drive_status.json。
    /// 只更新历史三字段，保留检查磁盘写入的容量/空闲/碎片率；盘符换了盘时按最新结果覆盖，
    /// 因为当前数据模型没有卷序列号，无法区分“同一盘符的新盘”。
    /// </summary>
    private static void UpdateDriveHistory(IReadOnlyList<DriveRunResult> results)
    {
        if (results.Count == 0) return;

        try
        {
            var file = DriveStatusFile.TryLoad() ?? new DriveStatusFile();
            foreach (var result in results)
            {
                var entry = file.Drives.FirstOrDefault(d =>
                    string.Equals(d.Drive, result.Drive, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    entry = new DriveStatusEntry { Drive = result.Drive };
                    file.Drives.Add(entry);
                }

                entry.LastRunAt = result.EndTime.ToString("yyyy-MM-dd HH:mm:ss");
                entry.LastRunStatus = result.Status;
                entry.LastErrorCode = result.ErrorCode ?? string.Empty;
            }

            file.Save();
        }
        catch (Exception ex)
        {
            Logger.Warn("更新 drive_status.json 历史字段失败: " + ex.Message);
        }
    }

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

    /// <summary>只保留非空行并限量保存 defrag 输出，避免日志过大。</summary>
    private sealed class OutputCapture
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();
        private bool _truncated;

        public void Append(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var value = line.Trim();
            lock (_gate)
            {
                if (_text.Length >= MaxCaptureChars)
                {
                    _truncated = true;
                    return;
                }

                if (_text.Length > 0) _text.Append(" | ");
                var remain = MaxCaptureChars - _text.Length;
                if (value.Length <= remain)
                {
                    _text.Append(value);
                }
                else
                {
                    _text.Append(value, 0, remain);
                    _truncated = true;
                }
            }
        }

        public string Text
        {
            get
            {
                lock (_gate) return _truncated ? _text + " …(已截断)" : _text.ToString();
            }
        }
    }

    private sealed record ProbeResult(
        double? FragPercent, int? ExitCode, string Output,
        int Suspended = 0, int Resumed = 0, bool Stopped = false);

    private sealed record ProbeGuardOutcome(int Suspended, int Resumed, bool Stopped);
}

/// <summary>单个磁盘的参数计划：Arguments 不含盘符，CommandLine 为完整命令行（不含敏感信息）。</summary>
public sealed record DefragPlan(
    string Drive,
    DiskMediaKind Media,
    string Arguments,
    string Verb,
    string Reason,
    int ClusterBytes)
{
    public string CommandLine => $"{Drive} {Arguments}";

    /// <summary>本次命令是否包含 /X（空闲空间合并）。</summary>
    public bool MergeRequested => Arguments.Contains("/X", StringComparison.OrdinalIgnoreCase);
}

/// <summary>机械盘空闲空间合并的门槛评估结果；Reason 直接用于日志/报告。</summary>
public sealed record MergeGate(
    bool Allowed,
    string Reason,
    double? FragPercent,
    double FreeRatio,
    double? FreeFragPercent);

/// <summary>/X 单步执行结果（与文件重写分开记录，失败不会被记成成功）。</summary>
public sealed record MergeRunResult(
    string Status,
    int? ExitCode,
    string? ErrorCode,
    int Suspended,
    int Resumed,
    DateTime StartTime,
    DateTime EndTime,
    string Detail)
{
    public double ElapsedSeconds => (EndTime - StartTime).TotalSeconds;
}

/// <summary>defrag /A 的只读探测结果；FragPercent 为 null 表示系统没有给出可用数值。</summary>
public sealed record ExternalFragResult(
    double? FragPercent,
    int? ExitCode,
    string? ErrorCode,
    bool Stopped,
    string Output);

public sealed record DriveRunResult(
    string Drive,
    DiskMediaKind Media,
    string Arguments,
    int? ExitCode,
    int Suspended,
    int Resumed,
    DateTime StartTime,
    DateTime EndTime,
    string Status,
    double? PreFrag,
    double? PostFrag,
    int ClusterBytes = 0,
    double? CoveragePercent = null,
    int Rounds = 0,
    string? ErrorCode = null,
    string MergeStatus = "未启用",
    string MergeDetail = "",
    double? WinFragBefore = null,
    double? WinFragAfter = null,
    string WinFragNote = "");
