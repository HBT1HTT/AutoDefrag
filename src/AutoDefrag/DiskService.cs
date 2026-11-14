using System.IO;
using System.Text.RegularExpressions;

namespace AutoDefrag;

/// <summary>磁盘介质类型；Unknown 表示无法识别，调用方按 SSD 保守处理。</summary>
public enum DiskMediaKind { Hdd, Ssd, Unknown }

public static class DiskService
{
    private static readonly Regex DriveSpecRegex = new(@"^[A-Za-z]:$", RegexOptions.Compiled);

    /// <summary>盘符白名单：只接受单个字母加冒号，拒绝 UNC、相对路径与多余字符。</summary>
    public static bool IsValidDriveSpec(string? text) =>
        !string.IsNullOrWhiteSpace(text) && DriveSpecRegex.IsMatch(text.Trim());

    /// <summary>只枚举本地固定磁盘（不含 U 盘 / 光驱 / 网络盘）。</summary>
    public static List<DriveInfo> GetFixedDrives()
    {
        var list = new List<DriveInfo>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                        list.Add(drive);
                }
                catch
                {
                    // 单盘读取失败不影响其它盘
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("枚举磁盘失败: " + ex.Message);
        }

        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Letter(DriveInfo drive) => drive.Name.TrimEnd('\\');

    public static string VolumeLabel(DriveInfo drive)
    {
        try
        {
            var label = drive.VolumeLabel;
            return string.IsNullOrWhiteSpace(label) ? "本地磁盘" : label;
        }
        catch
        {
            return "本地磁盘";
        }
    }

    public static string FileSystem(DriveInfo drive)
    {
        try
        {
            var fs = drive.DriveFormat;
            return string.IsNullOrWhiteSpace(fs) ? "未知文件系统" : fs;
        }
        catch
        {
            return "未知文件系统";
        }
    }

    /// <summary>磁盘类型（通俗说法），例如“机械硬盘”“固态硬盘”。</summary>
    public static string MediaText(DriveInfo drive) => MediaText(GetMediaKind(drive));

    /// <summary>文件系统与存储块大小，作为第二行补充信息。</summary>
    public static string DetailText(DriveInfo drive, int clusterBytes)
    {
        var fs = FileSystem(drive);
        return clusterBytes > 0 ? $"{fs} · 每块 {ClusterText(clusterBytes)}" : fs;
    }

    /// <summary>存储块大小文本，例如 "4KB" / "64KB"。</summary>
    public static string ClusterText(int clusterBytes)
    {
        if (clusterBytes <= 0) return "--";
        if (clusterBytes >= 1024 * 1024) return $"{clusterBytes / (1024.0 * 1024):0.#}MB";
        if (clusterBytes >= 1024) return $"{clusterBytes / 1024.0:0.#}KB";
        return $"{clusterBytes}B";
    }

    /// <summary>大簇（&gt;4KB）卷：Windows 内置 defrag 默认不作为主要整理途径，但并非绝对无效，可在设置中手动允许。</summary>
    public static bool IsLargeCluster(int clusterBytes) => clusterBytes > 4096;

    /// <summary>识别介质：寻道惩罚为 true=HDD，false=SSD，查询失败=Unknown。</summary>
    public static DiskMediaKind GetMediaKind(DriveInfo drive) =>
        Native.TryGetIncursSeekPenalty(Letter(drive)) switch
        {
            true => DiskMediaKind.Hdd,
            false => DiskMediaKind.Ssd,
            null => DiskMediaKind.Unknown
        };

    /// <summary>
    /// 卷是否位于虚拟/非物理盘（VHD/VHDX 的 file-backed virtual、虚拟总线、Storage Spaces）。
    /// true=虚拟；false=物理；null=无法识别。null 只能用于“按未知处理”，不能据此判失败。
    /// </summary>
    public static bool? IsVirtualVolume(string driveLetter) => Native.TryIsVirtualVolume(driveLetter);

    public static string MediaText(DiskMediaKind kind) => kind switch
    {
        DiskMediaKind.Hdd => "机械硬盘",
        DiskMediaKind.Ssd => "固态硬盘",
        _ => "类型未知"
    };

    /// <summary>可用空间，例如 "可用 1.2 TB"。</summary>
    public static string AvailableText(DriveInfo drive)
    {
        try { return $"可用 {FormatBytes(drive.AvailableFreeSpace)}"; }
        catch { return "可用 --"; }
    }

    /// <summary>总容量，例如 "共 1.8 TB"。</summary>
    public static string TotalText(DriveInfo drive)
    {
        try { return $"共 {FormatBytes(drive.TotalSize)}"; }
        catch { return "共 --"; }
    }

    public static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        const long tb = gb * 1024;

        if (bytes >= tb) return $"{bytes / (double)tb:0.##} TB";
        if (bytes >= gb) return $"{bytes / (double)gb:0.#} GB";
        if (bytes >= mb) return $"{bytes / (double)mb:0.#} MB";
        if (bytes >= kb) return $"{bytes / (double)kb:0.#} KB";
        return $"{bytes} B";
    }

    public static long TotalBytes(DriveInfo drive)
    {
        try { return drive.TotalSize; } catch { return 0; }
    }
}
