using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoDefrag;

/// <summary>
/// worker 每秒写一次的运行状态，供主界面实时显示（drive / stage / CPU / 挂起次数）。
/// </summary>
public sealed class WorkerStateFile
{
    public string Mode { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public string Drive { get; set; } = string.Empty;
    public int DriveIndex { get; set; }
    public int DriveTotal { get; set; }
    public double CpuPercent { get; set; }
    public int Suspended { get; set; }
    public int Resumed { get; set; }
    public string Message { get; set; } = string.Empty;
    public string StartedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var json = JsonSerializer.Serialize(this, JsonOptions);
            var tmp = AppPaths.WorkerStateFile + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            File.Move(tmp, AppPaths.WorkerStateFile, true);
        }
        catch
        {
            // 状态文件只是展示用途，失败不影响整理
        }
    }

    public static WorkerStateFile? TryLoad()
    {
        try
        {
            if (!File.Exists(AppPaths.WorkerStateFile)) return null;
            var text = File.ReadAllText(AppPaths.WorkerStateFile);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<WorkerStateFile>(text, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
