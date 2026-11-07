using System.IO;

namespace AutoDefrag;

public static class DiskService
{
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

    public static string TypeText(DriveInfo drive) => "本地固定 · " + FileSystem(drive);

    public static string SizeText(DriveInfo drive)
    {
        try
        {
            return $"{FormatBytes(drive.AvailableFreeSpace)} 可用 / {FormatBytes(drive.TotalSize)}";
        }
        catch
        {
            return "--";
        }
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
