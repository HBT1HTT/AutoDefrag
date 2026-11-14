using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace AutoDefrag;

/// <summary>
/// 配置模型。字段名与旧脚本 Data\config.json 保持一致，
/// 旧文件可以直接被读取；新增字段缺失时使用默认值。
/// </summary>
public sealed class AppConfig
{
    public int ConfigVersion { get; set; } = 10;

    /// <summary>CPU 守护阈值(%)：高于该值时挂起 defrag。</summary>
    public int CpuThreshold { get; set; } = 70;

    /// <summary>
    /// 每日整理时间；“0”表示不启用定时整理，回落到“每天开机后首次启动程序时整理一次”。
    /// 新安装默认使用开机首次启动；旧配置里已经填过合法时间的保持原样。
    /// </summary>
    public string DailyTime { get; set; } = "0";

    /// <summary>开机自启（写入 HKCU Run 键）。</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>通知停留时长（秒）。保留字段以兼容旧配置；Windows 通知实际展示时长由系统决定。</summary>
    public int NotificationTimeout { get; set; } = 30;

    /// <summary>每日调度总开关（托盘"暂停/恢复调度"也改这里）。</summary>
    public bool ScheduleEnabled { get; set; } = true;

    /// <summary>
    /// 勾选的磁盘盘符（如 ["C:","A:"]）。
    /// null 表示旧配置里没有该字段 —— 首次运行默认勾选全部本地固定磁盘。
    /// 空数组表示用户明确取消全选。
    /// </summary>
    public List<string>? SelectedDrives { get; set; } = null;

    /// <summary>关闭主窗口时最小化到托盘（false 则关闭即退出）。</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// 主题：System / Light / Dark。
    /// 1.9.6 起界面固定跟随 Windows 应用主题，本字段仅保留兼容旧配置，不再读取。
    /// </summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// 界面字体（系统已安装字体族名）。
    /// 1.9.6 起固定使用推荐默认字体（Segoe UI Variable Text + 中文回退），
    /// 本字段仅保留兼容旧配置，不再读取；旧值不会导致启动失败。
    /// </summary>
    public string UiFont { get; set; } = "";

    /// <summary>
    /// 整理方式：
    ///   Self   = 本程序整理（大容量存储块磁盘走文件级重写，小存储块仍用 defrag.exe）；
    ///   System = 只调用 Windows 自带 defrag.exe（v1.2.0 行为）；
    ///   Auto   = 按每块盘的存储块大小选择：&gt;4KB 用本程序整理，≤4KB 用 Windows 自带整理。
    /// 默认 Auto。注意：v1.3.0 已保存过 EngineMode 的配置会保留用户选择；
    /// v1.2.0 及更早的配置没有该字段，读取后按 Auto 处理（≤4KB 盘仍是系统整理，行为不突变）。
    /// </summary>
    public string EngineMode { get; set; } = "Auto";

    /// <summary>
    /// 主界面底部进度区是否展开（收起时只留一条细进度条 + 百分比）。
    /// 默认收起；切换后立即保存，重启保持。
    /// </summary>
    public bool ProgressExpanded { get; set; } = false;

    /// <summary>
    /// 只整理碎片较多的文件。
    /// false（默认）= 全部合格候选都处理；true = 只有文件级碎片率 ≥ FragThresholdPercent 的才处理。
    /// </summary>
    public bool FragThresholdEnabled { get; set; } = false;

    /// <summary>文件级碎片率阈值（%），范围 1–15，默认 10。超出范围在读取时回落默认值。</summary>
    public int FragThresholdPercent { get; set; } = 10;

    /// <summary>
    /// 整理质量预设：Fast / Balanced / Thorough / Custom。
    /// 前三档会整组套用参数；用户手动改过任意一项后为 Custom（保留用户值，不强行覆盖）。
    /// </summary>
    public string DefragQuality { get; set; } = DefragQualityPresets.Balanced;

    /// <summary>最小处理文件大小（KB），4–4096，默认 32（均衡档）。0 表示按“最小簇数”旧规则。</summary>
    public int MinFileSizeKB { get; set; } = 32;

    /// <summary>单轮最多处理文件数，10–500，默认 100（均衡档）。</summary>
    public int MaxFilesPerRun { get; set; } = 100;

    /// <summary>单轮最多复制数据量（GB），1–64，默认 20（均衡档）。</summary>
    public int MaxBytesPerRunGB { get; set; } = 20;

    /// <summary>单文件可回收碎片空间下限（MB），0–64，默认 4（均衡档）。</summary>
    public int MinReclaimMB { get; set; } = 4;

    /// <summary>整理后校验方式：Auto（≤2GB 全量，超过抽样）/ Full（总是全量）/ Sample（总是抽样首中尾 4MB）。</summary>
    public string VerifyMode { get; set; } = "Auto";

    /// <summary>多轮整理上限（每轮重新扫描候选），1–10，默认 4（均衡档）。</summary>
    public int MaxRounds { get; set; } = 4;

    /// <summary>整个任务总时长上限（分钟），0 = 不限，默认 180（均衡档）。达到上限后不再开始新的一轮。</summary>
    public int MaxRunMinutes { get; set; } = 180;

    /// <summary>
    /// 固态硬盘上的大簇卷只做系统优化（/O），不做文件级重写，避免无谓写入磨损。
    /// 默认开启；机械硬盘不受影响。仅“本程序整理/自动（大簇盘）”路径读取该开关。
    /// </summary>
    public bool SsdOptimizeOnly { get; set; } = true;

    /// <summary>
    /// 机械硬盘整理时合并空闲空间（defrag /X）。仅机械硬盘生效，固态与未知介质绝不执行。
    /// 默认开启；是否真正执行还要同时满足碎片率、空闲空间占比与空闲碎片度门槛，
    /// 不满足时只在日志/报告写明原因，不跑长时间的空闲搬移。
    /// </summary>
    public bool MergeFreeSpace { get; set; } = true;

    /// <summary>
    /// 手动覆盖“大簇卷不走系统整理”的限制：簇大小大于 4KB 时也允许使用 Windows 自带 defrag。
    /// 默认关闭；只有整理方式为“Windows 自带”或“自动”时才生效，选择“本程序整理”时不影响其文件级重写。
    /// </summary>
    public bool AllowLargeClusterSystem { get; set; }

    /// <summary>
    /// 整理任务真正完成时播放一段约 1 秒的提示音（失败、被停止时不播放）。
    /// 默认开启；旧配置没有该字段时按开启处理。
    /// </summary>
    public bool CompletionSound { get; set; } = true;

    public static readonly Regex DailyTimeRegex =
        new(@"^(0|00:00|([01]\d|2[0-3]):[0-5]\d)$", RegexOptions.Compiled);

    private static readonly Regex DriveRegex = new(@"^[A-Z]:$", RegexOptions.Compiled);

    [JsonIgnore]
    public bool RunAtStartup => DailyTime == "0" || DailyTime == "00:00";

    [JsonIgnore]
    public string EffectiveDailyTimeText => RunAtStartup ? "0" : DailyTime;

    public void Normalize()
    {
        if (CpuThreshold < 10 || CpuThreshold > 100) CpuThreshold = 70;
        if (!DailyTimeRegex.IsMatch(DailyTime ?? string.Empty)) DailyTime = "0";
        if (NotificationTimeout < 10 || NotificationTimeout > 120) NotificationTimeout = 30;
        if (Theme is not ("System" or "Light" or "Dark")) Theme = "System";
        if (EngineMode is not ("Self" or "System" or "Auto")) EngineMode = "Auto";
        if (FragThresholdPercent < 1 || FragThresholdPercent > 15) FragThresholdPercent = 10;
        if (!DefragQualityPresets.IsValid(DefragQuality)) DefragQuality = DefragQualityPresets.Balanced;
        if (MinFileSizeKB < 4 || MinFileSizeKB > 4096) MinFileSizeKB = 32;
        if (MaxFilesPerRun < 10 || MaxFilesPerRun > 500) MaxFilesPerRun = 100;
        if (MaxBytesPerRunGB < 1 || MaxBytesPerRunGB > 64) MaxBytesPerRunGB = 20;
        if (MinReclaimMB < 0 || MinReclaimMB > 64) MinReclaimMB = 4;
        if (VerifyMode is not ("Auto" or "Full" or "Sample")) VerifyMode = "Auto";
        if (MaxRounds < 1 || MaxRounds > 10) MaxRounds = 4;
        if (MaxRunMinutes < 0 || MaxRunMinutes > 1440) MaxRunMinutes = 180;
        UiFont = (UiFont ?? string.Empty).Trim();

        if (SelectedDrives is not null)
        {
            var normalized = new List<string>();
            foreach (var raw in SelectedDrives)
            {
                var letter = (raw ?? string.Empty).Trim().TrimEnd('\\').ToUpperInvariant();
                if (DriveRegex.IsMatch(letter) && !normalized.Contains(letter))
                    normalized.Add(letter);
            }
            SelectedDrives = normalized;
        }
    }

    /// <summary>当前配置对应的整理偏好（界面草稿与 worker 参数都从这里取）。</summary>
    public DefragPreferences GetDefragPreferences() => new(
        DefragQuality,
        MinFileSizeKB, MaxFilesPerRun, MaxBytesPerRunGB, MinReclaimMB,
        VerifyMode, MaxRounds, MaxRunMinutes, SsdOptimizeOnly, MergeFreeSpace, AllowLargeClusterSystem);

    /// <summary>
    /// 写回整理偏好：先规范化（非法值回落默认），再同步到字段。
    /// 文件碎片率阈值是独立设置（设置窗口单独写回），不在这里被档位覆盖。
    /// </summary>
    public void ApplyDefragPreferences(DefragPreferences prefs)
    {
        var p = prefs.Normalized();
        DefragQuality = p.Quality;
        MinFileSizeKB = p.MinFileSizeKB;
        MaxFilesPerRun = p.MaxFilesPerRun;
        MaxBytesPerRunGB = p.MaxBytesPerRunGB;
        MinReclaimMB = p.MinReclaimMB;
        VerifyMode = p.VerifyMode;
        MaxRounds = p.MaxRounds;
        MaxRunMinutes = p.MaxRunMinutes;
        SsdOptimizeOnly = p.SsdOptimizeOnly;
        MergeFreeSpace = p.MergeFreeSpace;
        AllowLargeClusterSystem = p.AllowLargeClusterSystem;
        Normalize();
    }
}

/// <summary>整理偏好快照：设置窗口、配置迁移与 worker 参数共用同一份结构。</summary>
public sealed record DefragPreferences(
    string Quality,
    int MinFileSizeKB,
    int MaxFilesPerRun,
    int MaxBytesPerRunGB,
    int MinReclaimMB,
    string VerifyMode,
    int MaxRounds,
    int MaxRunMinutes,
    bool SsdOptimizeOnly,
    bool MergeFreeSpace = true,
    bool AllowLargeClusterSystem = false)
{
    public static DefragPreferences Default => DefragQualityPresets.Get(DefragQualityPresets.Balanced).ToPreferences();

    /// <summary>逐字段夹紧到允许范围；只用于修正非法输入，不改变用户选定的档位。</summary>
    public DefragPreferences Normalized() => this with
    {
        Quality = DefragQualityPresets.IsValid(Quality) ? Quality : DefragQualityPresets.Balanced,
        MinFileSizeKB = MinFileSizeKB is >= 4 and <= 4096 ? MinFileSizeKB : 32,
        MaxFilesPerRun = MaxFilesPerRun is >= 10 and <= 500 ? MaxFilesPerRun : 100,
        MaxBytesPerRunGB = MaxBytesPerRunGB is >= 1 and <= 64 ? MaxBytesPerRunGB : 20,
        MinReclaimMB = MinReclaimMB is >= 0 and <= 64 ? MinReclaimMB : 4,
        VerifyMode = VerifyMode is "Auto" or "Full" or "Sample" ? VerifyMode : "Auto",
        MaxRounds = MaxRounds is >= 1 and <= 10 ? MaxRounds : 4,
        MaxRunMinutes = MaxRunMinutes is >= 0 and <= 1440 ? MaxRunMinutes : 180
    };
}

/// <summary>一档整理质量预设：整组参数一次套用，保证各字段之间互相匹配。</summary>
public sealed record DefragQualityPreset(
    string Key,
    string Name,
    string Summary,
    int MinFileSizeKB,
    int MaxFilesPerRun,
    int MaxBytesPerRunGB,
    int MinReclaimMB,
    string VerifyMode,
    int MaxRounds,
    int MaxRunMinutes)
{
    public bool IsNamed => Key is "Fast" or "Balanced" or "Thorough";

    public DefragPreferences ToPreferences() => new(
        Key, MinFileSizeKB, MaxFilesPerRun, MaxBytesPerRunGB, MinReclaimMB,
        VerifyMode, MaxRounds, MaxRunMinutes, SsdOptimizeOnly: true, MergeFreeSpace: true,
        AllowLargeClusterSystem: false);
}

/// <summary>
/// 三档预设的数值与依据（这三档是“最小文件/单轮上限/可回收下限/校验/轮次/时长”的唯一入口）：
/// 最快   —— 只处理体积大（≥256KB）、收益明显（≥16MB）的文件，单轮 20 文件/4GB，1 轮，
///           抽样校验；候选池最小，适合“先快速缓解卡顿”。
/// 均衡   —— 默认档。32KB 门槛、单轮 100 文件/20GB、最多 4 轮、自动校验（≤2GB 全量，超过抽样）；
///           正常候选跑完后自动进入“补扫轮”，把被可回收下限挡掉的小碎片也纳入，直到收敛或达到轮次上限。
/// 最彻底 —— 4KB 门槛（64KB 簇盘按 1 簇=64KB 放宽）、单轮 400 文件/48GB、最多 8 轮、全量校验；
///           读放大最高，耗时最长，建议空闲时运行。
/// </summary>
public static class DefragQualityPresets
{
    public const string Fast = "Fast";
    public const string Balanced = "Balanced";
    public const string Thorough = "Thorough";
    public const string Custom = "Custom";

    public static readonly DefragQualityPreset FastPreset = new(
        Fast, "最快",
        "只处理体积大、收益高的文件；1 轮、抽样校验。耗时最短，适合快速缓解卡顿。",
        MinFileSizeKB: 256, MaxFilesPerRun: 20, MaxBytesPerRunGB: 4, MinReclaimMB: 16,
        VerifyMode: "Sample", MaxRounds: 1, MaxRunMinutes: 30);

    public static readonly DefragQualityPreset BalancedPreset = new(
        Balanced, "均衡",
        "默认。32KB 门槛、单轮 100 文件/20GB、最多 4 轮、自动校验；自动补扫小碎片直到收敛。",
        MinFileSizeKB: 32, MaxFilesPerRun: 100, MaxBytesPerRunGB: 20, MinReclaimMB: 4,
        VerifyMode: "Auto", MaxRounds: 4, MaxRunMinutes: 180);

    public static readonly DefragQualityPreset ThoroughPreset = new(
        Thorough, "最彻底",
        "门槛最低、最多 8 轮、全量校验；自动补扫小碎片直到收敛。尽量少碎片，耗时最长，建议空闲时使用。",
        MinFileSizeKB: 4, MaxFilesPerRun: 400, MaxBytesPerRunGB: 48, MinReclaimMB: 1,
        VerifyMode: "Full", MaxRounds: 8, MaxRunMinutes: 480);

    public static IReadOnlyList<DefragQualityPreset> Named => new[]
    {
        FastPreset, BalancedPreset, ThoroughPreset
    };

    public static bool IsValid(string? key) =>
        key is Fast or Balanced or Thorough or Custom;

    /// <summary>按 key 取预设；未知 key（含 Custom）回落到均衡档，避免读取时抛异常。</summary>
    public static DefragQualityPreset Get(string? key) => key switch
    {
        Fast => FastPreset,
        Thorough => ThoroughPreset,
        _ => BalancedPreset
    };

    /// <summary>当前参数与哪一档完全一致；都不一致返回 null（旧配置中的“自定义”）。</summary>
    public static DefragQualityPreset? Match(DefragPreferences prefs)
    {
        foreach (var preset in Named)
        {
            if (EqualsPreset(preset, prefs)) return preset;
        }
        return null;
    }

    /// <summary>
    /// 旧版“自定义”配置迁移用：按数值距离找最接近的一档。
    /// 各项按相对偏差比较，避免“文件数 500”这种大数值单方面主导结果。
    /// </summary>
    public static DefragQualityPreset Nearest(DefragPreferences p)
    {
        DefragQualityPreset? best = null;
        var bestDistance = double.MaxValue;

        foreach (var preset in Named)
        {
            double distance = 0;
            distance += RatioDistance(p.MinFileSizeKB, preset.MinFileSizeKB);
            distance += RatioDistance(p.MaxFilesPerRun, preset.MaxFilesPerRun);
            distance += RatioDistance(p.MaxBytesPerRunGB, preset.MaxBytesPerRunGB);
            distance += RatioDistance(p.MinReclaimMB, preset.MinReclaimMB);
            distance += RatioDistance(p.MaxRounds, preset.MaxRounds);
            distance += RatioDistance(p.MaxRunMinutes == 0 ? 1440 : p.MaxRunMinutes,
                preset.MaxRunMinutes == 0 ? 1440 : preset.MaxRunMinutes);
            if (p.VerifyMode != preset.VerifyMode) distance += 0.5;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = preset;
            }
        }

        return best ?? BalancedPreset;
    }

    private static double RatioDistance(double value, double reference)
    {
        var max = Math.Max(Math.Abs(value), Math.Abs(reference));
        if (max <= 0) return 0;
        return Math.Abs(value - reference) / max;
    }

    public static bool EqualsPreset(DefragQualityPreset preset, DefragPreferences p) =>
        p.MinFileSizeKB == preset.MinFileSizeKB
        && p.MaxFilesPerRun == preset.MaxFilesPerRun
        && p.MaxBytesPerRunGB == preset.MaxBytesPerRunGB
        && p.MinReclaimMB == preset.MinReclaimMB
        && p.VerifyMode == preset.VerifyMode
        && p.MaxRounds == preset.MaxRounds
        && p.MaxRunMinutes == preset.MaxRunMinutes;
}
