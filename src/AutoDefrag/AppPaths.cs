using System.IO;

namespace AutoDefrag;

/// <summary>
/// 运行时路径全部由 exe 所在目录 / %LocalAppData% 推导，不写死任何本机绝对路径。
/// 优先把 Data、Logs 放在 exe 同级目录（便于从旧脚本目录无缝复用配置）；
/// 若 exe 目录不可写（例如装在 Program Files），退回到 %LocalAppData%\AutoDefrag。
/// </summary>
public static class AppPaths
{
    public static string ExePath { get; } =
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AutoDefrag.exe");

    public static string ExeDir { get; } =
        Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    public static string Root { get; } = ChooseRoot();

    public static string DataDir => Path.Combine(Root, "Data");
    public static string LogDir => Path.Combine(Root, "Logs");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LastResultFile => Path.Combine(DataDir, "last_result.txt");
    public static string DriveStatusFile => Path.Combine(DataDir, "drive_status.json");
    public static string LastRunFile => Path.Combine(DataDir, "last_run.txt");
    public static string WorkerStateFile => Path.Combine(DataDir, "worker_state.json");

    /// <summary>旧脚本放在 exe 目录 Data\config.json；若 Root 退回 LocalAppData 时用它做一次性迁移。</summary>
    public static string LegacyConfigFile => Path.Combine(ExeDir, "Data", "config.json");
    public static string LegacyLastRunFile => Path.Combine(ExeDir, "Data", "last_run.txt");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
    }

    private static string ChooseRoot()
    {
        try
        {
            var data = Path.Combine(ExeDir, "Data");
            var logs = Path.Combine(ExeDir, "Logs");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(logs);

            // 真正验证可写，避免"目录能建但文件写不进"的半可写场景
            var probe = Path.Combine(data, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return ExeDir;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutoDefrag");
            Directory.CreateDirectory(Path.Combine(fallback, "Data"));
            Directory.CreateDirectory(Path.Combine(fallback, "Logs"));
            return fallback;
        }
    }
}
