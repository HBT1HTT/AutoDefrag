namespace AutoDefrag;

/// <summary>
/// 整理方式决策：
///   Self   = 本程序整理（>4KB 卷走文件级重写；≤4KB 卷仍调用系统 defrag）；
///   System = 只调用 Windows 自带 defrag；
///   Auto   = 按每块盘的存储块大小自动选：&gt;4KB 用本程序整理，≤4KB 用 Windows 自带整理。
/// 决策文本带存储块大小，作为“判断依据”写入界面、计划、日志和 last_result。
/// </summary>
public static class EngineResolver
{
    public const string Auto = "Auto";
    public const string Self = "Self";
    public const string System = "System";

    public static bool IsValid(string? mode) =>
        mode is Self or System or Auto;

    /// <summary>本轮该盘实际采用的引擎（Auto 会按簇大小收敛成 Self / System）。</summary>
    public static string Effective(string? mode, int clusterBytes)
    {
        var requested = IsValid(mode) ? mode! : Auto;
        if (requested == Auto)
            return DiskService.IsLargeCluster(clusterBytes) ? Self : System;
        return requested;
    }

    public static string ShortName(string effective) =>
        effective == System ? "Windows 自带整理" : "本程序整理";

    /// <summary>展示文本，例如“自动 → 本程序整理（存储块 64KB）”。</summary>
    public static string Describe(string? mode, int clusterBytes)
    {
        var requested = IsValid(mode) ? mode! : Auto;
        var large = DiskService.IsLargeCluster(clusterBytes);
        var clusterText = DiskService.ClusterText(clusterBytes);

        if (requested == Auto)
        {
            return large
                ? $"自动 → 本程序整理（存储块 {clusterText}，大于 4KB）"
                : $"自动 → Windows 自带整理（存储块 {clusterText}，不超过 4KB）";
        }

        if (requested == System)
        {
            return large
                ? $"Windows 自带整理（存储块 {clusterText}，该磁盘系统方式无效）"
                : $"Windows 自带整理（存储块 {clusterText}）";
        }

        return large
            ? $"本程序整理（存储块 {clusterText}，文件级重写）"
            : $"本程序整理（存储块 {clusterText}，小存储块调用系统整理）";
    }

    public static string ModeText(string? mode) => (IsValid(mode) ? mode! : Auto) switch
    {
        System => "Windows 自带整理",
        Auto => "自动（按存储块选择）",
        _ => "本程序整理"
    };
}
