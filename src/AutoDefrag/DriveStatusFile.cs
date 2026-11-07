using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoDefrag;

/// <summary>只读探测产物 Data\drive_status.json（字段与旧脚本一致）。</summary>
public sealed class DriveStatusFile
{
    public string ProbeTime { get; set; } = string.Empty;
    public List<DriveStatusEntry> Drives { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static DriveStatusFile? TryLoad()
    {
        try
        {
            if (!File.Exists(AppPaths.DriveStatusFile)) return null;
            var text = File.ReadAllText(AppPaths.DriveStatusFile);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<DriveStatusFile>(text, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            var json = JsonSerializer.Serialize(this, JsonOptions);
            var tmp = AppPaths.DriveStatusFile + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            File.Move(tmp, AppPaths.DriveStatusFile, true);
        }
        catch (Exception ex)
        {
            Logger.Warn("写 drive_status.json 失败: " + ex.Message);
        }
    }
}

public sealed class DriveStatusEntry
{
    public string Drive { get; set; } = string.Empty;
    public double? VolumeSizeGB { get; set; }
    public double? FreeSpaceGB { get; set; }
    public double? FragPercent { get; set; }
    public bool Probed { get; set; }
}
