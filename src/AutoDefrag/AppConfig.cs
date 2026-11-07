using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace AutoDefrag;

/// <summary>
/// 配置模型。字段名与旧脚本 Data\config.json 保持一致，
/// 旧文件可以直接被读取；新增字段缺失时使用默认值。
/// </summary>
public sealed class AppConfig
{
    public int ConfigVersion { get; set; } = 3;

    /// <summary>CPU 守护阈值(%)：高于该值时挂起 defrag。</summary>
    public int CpuThreshold { get; set; } = 70;

    /// <summary>每日执行时间 HH:mm；"0" / "00:00" 表示"程序启动后立即执行一次"。</summary>
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

    /// <summary>
    /// 界面字体（系统已安装字体族名，例如 "Microsoft YaHei UI"）。
    /// 空字符串表示使用推荐默认字体（Segoe UI Variable Text + 中文回退）。
    /// </summary>
    public string UiFont { get; set; } = "";

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
}
