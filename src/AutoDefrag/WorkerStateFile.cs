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

    /// <summary>本轮该盘实际采用的整理方式（短名），例如“本程序整理”。</summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>方式判断依据，例如“自动 → 本程序整理（存储块 64KB，大于 4KB）”。</summary>
    public string EngineDetail { get; set; } = string.Empty;

    /// <summary>磁盘对象名（不含冒号），便于显示。</summary>
    public string DriveLabel { get; set; } = string.Empty;

    /// <summary>进度百分比 0–100；-1 表示无法确定（不要显示假进度）。</summary>
    public int Percent { get; set; } = -1;

    /// <summary>当前阶段内的子阶段：分析 / 复制 / 校验 / 替换。</summary>
    public string Phase { get; set; } = string.Empty;

    public string CurrentFile { get; set; } = string.Empty;
    public int FileIndex { get; set; }
    public int FileTotal { get; set; }

    /// <summary>已处理字节 / 候选总字节（本程序整理时按计划字节数计算）。</summary>
    public long ProcessedBytes { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>本盘本轮平均速度（字节/秒）；0 表示未知。</summary>
    public long SpeedBytesPerSec { get; set; }

    public string StartedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    private static readonly object SaveGate = new();

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
            // 系统 defrag 输出回调与主循环可能同时刷新状态，这里串行化写入
            lock (SaveGate)
            {
                File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
                File.Move(tmp, AppPaths.WorkerStateFile, true);
            }
        }
        catch
        {
            // 状态文件只是展示用途，失败不影响整理
        }
    }

    /// <summary>更新阶段与提示并立即落盘；重写流程用它刷新界面进度。</summary>
    public void SetStage(string stage, string message)
    {
        Stage = stage;
        Message = message;
        Save();
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
