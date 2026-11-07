using System.IO;
using System.Text;

namespace AutoDefrag;

/// <summary>
/// 简单文本日志：主进程写 Logs\app.log，后台 worker 写 Logs\engine.log。
/// 与旧脚本的日志格式保持一致，方便对照排查。
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static string _source = "app";

    public static void Init(string source) => _source = source;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", message + " : " + ex);

    private static void Write(string level, string message)
    {
        try
        {
            AppPaths.Ensure();
            var file = Path.Combine(AppPaths.LogDir, _source + ".log");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] [{_source}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(file, line, Utf8NoBom);
            }
        }
        catch
        {
            // 日志永不应该影响主流程
        }
    }

    public static string AppLogPath => Path.Combine(AppPaths.LogDir, "app.log");
}
