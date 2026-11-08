using System.IO;
using System.Text;

namespace AutoDefrag;

/// <summary>
/// 进度日志 Data/progress.log：worker / 预览写入，主界面按偏移量增量读取，只保留最近 N 行。
/// 与 Logs/engine.log 分开，避免界面为了看进度读整个引擎日志。
/// </summary>
public static class ProgressLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.DataDir, "progress.log");

    /// <summary>开始新任务：清空旧进度，避免界面把上次的日志当成这次。</summary>
    public static void Begin()
    {
        try
        {
            AppPaths.Ensure();
            lock (Gate) File.WriteAllText(FilePath, string.Empty, Utf8NoBom);
        }
        catch
        {
            // 进度日志只用于展示，失败不影响整理
        }
    }

    public static void Append(string message)
    {
        var text = (message ?? string.Empty).Trim();
        if (text.Length == 0) return;

        try
        {
            AppPaths.Ensure();
            var line = $"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}";
            lock (Gate)
            {
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(Utf8NoBom.GetBytes(line));
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>从 offset 起读取新增行；文件被新任务截断时自动从头读。</summary>
    public static (List<string> Lines, long Offset) ReadNew(long offset)
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(FilePath)) return (lines, offset);

            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset > stream.Length) offset = 0;
            stream.Position = offset;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var end = stream.Position;

            if (text.Length > 0)
            {
                var parts = text.Replace("\r\n", "\n").Split('\n');
                foreach (var part in parts)
                {
                    if (part.Length > 0) lines.Add(part);
                }
            }

            return (lines, end);
        }
        catch
        {
            return (lines, offset);
        }
    }
}
