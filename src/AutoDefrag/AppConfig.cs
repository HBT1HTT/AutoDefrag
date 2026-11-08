using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace AutoDefrag;

/// <summary>
/// 配置模型。字段名与旧脚本 Data\config.json 保持一致，
/// 旧文件可以直接被读取；新增字段缺失时使用默认值。
/// </summary>
public sealed class AppConfig
{
    public int ConfigVersion { get; set; } = 6;

    /// <summary>CPU 守护阈值(%)：高于该值时挂起 defrag。</summary>
    public int CpuThreshold { get; set; } = 70;

    /// <summary>每日执行时间 HH:mm；"0" / "00:00" 表示"当天首次启动时执行一次"。</summary>
    public string DailyTime { get; set; } = "03:00";

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

    /// <summary>主题：System / Light / Dark。</summary>
    public string Theme { get; set; } = "System";

    /// <summary>界面字体（系统已安装字体族名，例如 "Microsoft YaHei UI"）。
    /// 空字符串表示使用推荐默认字体（Segoe UI Variable Text + 中文回退）。
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

    /// <summary>单轮最多处理文件数，10–500，默认 80（均衡档）。</summary>
    public int MaxFilesPerRun { get; set; } = 80;

    /// <summary>单轮最多复制数据量（GB），1–64，默认 16（均衡档）。</summary>
    public int MaxBytesPerRunGB { get; set; } = 16;

    /// <summary>单文件可回收碎片空间下限（MB），0–64，默认 4（均衡档）。</summary>
    public int MinReclaimMB { get; set; } = 4;

    /// <summary>整理后校验方式：Auto（≤2GB 全量，超过抽样）/ Full（总是全量）/ Sample（总是抽样首中尾 4MB）。</summary>
    public string VerifyMode { get; set; } = "Auto";

    /// <summary>多轮整理上限（每轮重新扫描候选），1–10，默认 3（均衡档）。</summary>
    public int MaxRounds { get; set; } = 3;

    /// <summary>整个任务总时长上限（分钟），0 = 不限，默认 120（均衡档）。达到上限后不再开始新的一轮。</summary>
    public int MaxRunMinutes { get; set; } = 120;

    /// <summary>
    /// 固态硬盘上的大簇卷只做系统优化（/O），不做文件级重写，避免无谓写入磨损。
    /// 默认开启；机械硬盘不受影响。仅“本程序整理/自动（大簇盘）”路径读取该开关。
    /// </summary>
    public bool SsdOptimizeOnly { get; set; } = true;

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
        if (!DailyTimeRegex.IsMatch(DailyTime ?? string.Empty)) DailyTime = "03:00";
        if (NotificationTimeout < 10 || NotificationTimeout > 120) NotificationTimeout = 30;
        if (Theme is not ("System" or "Light" or "Dark")) Theme = "System";
        if (EngineMode is not ("Self" or "System" or "Auto")) EngineMode = "Auto";
        if (FragThresholdPercent < 1 || FragThresholdPercent > 15) FragThresholdPercent = 10;
        if (!DefragQualityPresets.IsValid(DefragQuality)) DefragQuality = DefragQualityPresets.Balanced;
        if (MinFileSizeKB < 4 || MinFileSizeKB > 4096) MinFileSizeKB = 32;
        if (MaxFilesPerRun < 10 || MaxFilesPerRun > 500) MaxFilesPerRun = 80;
        if (MaxBytesPerRunGB < 1 || MaxBytesPerRunGB > 64) MaxBytesPerRunGB = 16;
        if (MinReclaimMB < 0 || MinReclaimMB > 64) MinReclaimMB = 4;
        if (VerifyMode is not ("Auto" or "Full" or "Sample")) VerifyMode = "Auto";
        if (MaxRounds < 1 || MaxRounds > 10) MaxRounds = 3;
        if (MaxRunMinutes < 0 || MaxRunMinutes > 1440) MaxRunMinutes = 120;
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
        DefragQuality, FragThresholdEnabled, FragThresholdPercent,
        MinFileSizeKB, MaxFilesPerRun, MaxBytesPerRunGB, MinReclaimMB,
        VerifyMode, MaxRounds, MaxRunMinutes, SsdOptimizeOnly);

    /// <summary>写回整理偏好：先规范化（非法值回落默认），再同步到字段。</summary>
    public void ApplyDefragPreferences(DefragPreferences prefs)
    {
        var p = prefs.Normalized();
        DefragQuality = p.Quality;
        FragThresholdEnabled = p.FragThresholdEnabled;
        FragThresholdPercent = p.FragThresholdPercent;
        MinFileSizeKB = p.MinFileSizeKB;
        MaxFilesPerRun = p.MaxFilesPerRun;
        MaxBytesPerRunGB = p.MaxBytesPerRunGB;
        MinReclaimMB = p.MinReclaimMB;
        VerifyMode = p.VerifyMode;
        MaxRounds = p.MaxRounds;
        MaxRunMinutes = p.MaxRunMinutes;
        SsdOptimizeOnly = p.SsdOptimizeOnly;
        Normalize();
    }
}

/// <summary>整理偏好快照：设置窗口、配置迁移与 worker 参数共用同一份结构。</summary>
public sealed record DefragPreferences(
    string Quality,
    bool FragThresholdEnabled,
    int FragThresholdPercent,
    int MinFileSizeKB,
    int MaxFilesPerRun,
    int MaxBytesPerRunGB,
    int MinReclaimMB,
    string VerifyMode,
    int MaxRounds,
    int MaxRunMinutes,
    bool SsdOptimizeOnly)
{
    public static DefragPreferences Default => DefragQualityPresets.Get(DefragQualityPresets.Balanced).ToPreferences();

    /// <summary>逐字段夹紧到允许范围；只用于修正非法输入，不改变用户选定的档位。</summary>
    public DefragPreferences Normalized() => this with
    {
        Quality = DefragQualityPresets.IsValid(Quality) ? Quality : DefragQualityPresets.Balanced,
        FragThresholdPercent = FragThresholdPercent is >= 1 and <= 15 ? FragThresholdPercent : 10,
        MinFileSizeKB = MinFileSizeKB is >= 4 and <= 4096 ? MinFileSizeKB : 32,
        MaxFilesPerRun = MaxFilesPerRun is >= 10 and <= 500 ? MaxFilesPerRun : 80,
        MaxBytesPerRunGB = MaxBytesPerRunGB is >= 1 and <= 64 ? MaxBytesPerRunGB : 16,
        MinReclaimMB = MinReclaimMB is >= 0 and <= 64 ? MinReclaimMB : 4,
        VerifyMode = VerifyMode is "Auto" or "Full" or "Sample" ? VerifyMode : "Auto",
        MaxRounds = MaxRounds is >= 1 and <= 10 ? MaxRounds : 3,
        MaxRunMinutes = MaxRunMinutes is >= 0 and <= 1440 ? MaxRunMinutes : 120
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
    bool FragThresholdEnabled,
    int FragThresholdPercent,
    int MaxRounds,
    int MaxRunMinutes)
{
    public bool IsNamed => Key is "Fast" or "Balanced" or "Thorough";

    public DefragPreferences ToPreferences() => new(
        Key, FragThresholdEnabled, FragThresholdPercent,
        MinFileSizeKB, MaxFilesPerRun, MaxBytesPerRunGB, MinReclaimMB,
        VerifyMode, MaxRounds, MaxRunMinutes, SsdOptimizeOnly: true);
}

/// <summary>
/// 三档预设的数值与依据：
/// 最快   —— 只处理碎片率高（≥10%）、体积大（≥256KB）、收益明显（≥16MB）的文件，单轮 20 文件/4GB，
///           抽样校验；候选池最小，适合“先快速缓解卡顿”。
/// 均衡   —— 默认档。32KB 门槛、单轮 80 文件/16GB、最多 3 轮、自动校验（≤2GB 全量，超过抽样）；
///           大致是旧版单批 40 文件/8GB 的两倍覆盖，叠加最多 3 轮。
/// 最彻底 —— 4KB 门槛（64KB 簇盘仍按 1 簇=64KB）、单轮 300 文件/32GB、最多 8 轮、全量校验；
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
        "只处理碎片率高、体积大的文件；单轮 1 轮、抽样校验。耗时最短，适合快速缓解卡顿。",
        MinFileSizeKB: 256, MaxFilesPerRun: 20, MaxBytesPerRunGB: 4, MinReclaimMB: 16,
        VerifyMode: "Sample", FragThresholdEnabled: true, FragThresholdPercent: 10,
        MaxRounds: 1, MaxRunMinutes: 30);

    public static readonly DefragQualityPreset BalancedPreset = new(
        Balanced, "均衡",
        "默认。32KB 门槛、单轮 80 文件/16GB、最多 3 轮、自动校验。覆盖与耗时折中。",
        MinFileSizeKB: 32, MaxFilesPerRun: 80, MaxBytesPerRunGB: 16, MinReclaimMB: 4,
        VerifyMode: "Auto", FragThresholdEnabled: false, FragThresholdPercent: 10,
        MaxRounds: 3, MaxRunMinutes: 120);

    public static readonly DefragQualityPreset ThoroughPreset = new(
        Thorough, "最彻底",
        "门槛最低、最多 8 轮、全量校验。尽量少碎片，耗时最长，建议空闲时使用。",
        MinFileSizeKB: 4, MaxFilesPerRun: 300, MaxBytesPerRunGB: 32, MinReclaimMB: 1,
        VerifyMode: "Full", FragThresholdEnabled: false, FragThresholdPercent: 5,
        MaxRounds: 8, MaxRunMinutes: 480);

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

    /// <summary>当前参数与哪一档完全一致；都不一致返回 null（界面显示“自定义”）。</summary>
    public static DefragQualityPreset? Match(DefragPreferences prefs)
    {
        foreach (var preset in Named)
        {
            if (EqualsPreset(preset, prefs)) return preset;
        }
        return null;
    }

    public static bool EqualsPreset(DefragQualityPreset preset, DefragPreferences p) =>
        p.MinFileSizeKB == preset.MinFileSizeKB
        && p.MaxFilesPerRun == preset.MaxFilesPerRun
        && p.MaxBytesPerRunGB == preset.MaxBytesPerRunGB
        && p.MinReclaimMB == preset.MinReclaimMB
        && p.VerifyMode == preset.VerifyMode
        && p.FragThresholdEnabled == preset.FragThresholdEnabled
        && p.FragThresholdPercent == preset.FragThresholdPercent
        && p.MaxRounds == preset.MaxRounds
        && p.MaxRunMinutes == preset.MaxRunMinutes;
}
