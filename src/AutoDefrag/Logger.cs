using System.IO;
using System.Text;

namespace AutoDefrag;

/// <summary>
/// 简单文本日志：主进程写 Logs\app.log，后台 worker 写 Logs\engine.log。
/// 与旧脚本的日志格式保持一致，方便对照排查。
/// </summary>
public static class Logger
{
    private const long MaxBytes = 2 * 1024 * 1024; // 单文件上限 2 MB，保留 1 个 .1 备份

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
                RotateIfNeeded(file);
                File.AppendAllText(file, line, Utf8NoBom);
            }
        }
        catch
        {
            // 日志永不应该影响主流程
        }
    }

    /// <summary>日志轮转：超过上限时把当前文件改名为 .1（覆盖旧备份），避免日志无限增长。</summary>
    private static void RotateIfNeeded(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length < MaxBytes) return;

            var backup = file + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(file, backup);
        }
        catch
        {
            // 轮转失败时保持原文件继续追加，不能因此丢掉日志
        }
    }

    public static string AppLogPath => Path.Combine(AppPaths.LogDir, "app.log");
}
