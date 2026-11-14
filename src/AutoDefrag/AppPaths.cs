using System.IO;

namespace AutoDefrag;

/// <summary>
/// 运行时路径推导：配置、日志、状态、报告、停止标记全部放在
/// %LocalAppData%\AutoDefrag 下，exe 同目录不再产生任何文件或目录（便携 exe 可以单独放着）。
/// 首次运行时把旧版（≤1.6.0，数据在 exe 同目录 Data\Logs）的数据迁移过来。
/// </summary>
public static class AppPaths
{
    private const string FolderName = "AutoDefrag";
    private static readonly object MigrationGate = new();
    private static bool _migrationDone;

    public static string ExePath { get; } =
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AutoDefrag.exe");

    public static string ExeDir { get; } =
        Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    /// <summary>旧版数据根目录（exe 同目录）。只在一次性迁移与降级提示里使用，不再写入。</summary>
    public static string LegacyRoot => ExeDir;
    public static string LegacyDataDir => Path.Combine(ExeDir, "Data");
    public static string LegacyLogDir => Path.Combine(ExeDir, "Logs");

    /// <summary>运行数据根目录；正常情况恒为 %LocalAppData%\AutoDefrag。</summary>
    public static string Root => _root ??= ChooseRoot();

    private static string? _root;

    public static string DataDir => Path.Combine(Root, "Data");
    public static string LogDir => Path.Combine(Root, "Logs");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string LastResultFile => Path.Combine(DataDir, "last_result.txt");
    public static string DriveStatusFile => Path.Combine(DataDir, "drive_status.json");
    public static string LastRunFile => Path.Combine(DataDir, "last_run.txt");
    public static string WorkerStateFile => Path.Combine(DataDir, "worker_state.json");

    /// <summary>大簇卷文件级重写报告。</summary>
    public static string RewriteReportFile => Path.Combine(DataDir, "rewrite_report.txt");

    /// <summary>dry-run 预览报告（与真实执行结果分开存放）。</summary>
    public static string RewritePreviewFile => Path.Combine(DataDir, "rewrite_preview.txt");

    /// <summary>存在该文件时，重写在下一个文件前体面停止。</summary>
    public static string StopFlagFile => Path.Combine(DataDir, "rewrite_stop.flag");

    /// <summary>旧脚本 / 1.6.0 及更早版本放在 exe 目录的配置；仅作迁移失败时的保底读取。</summary>
    public static string LegacyConfigFile => Path.Combine(LegacyDataDir, "config.json");
    public static string LegacyLastRunFile => Path.Combine(LegacyDataDir, "last_run.txt");

    /// <summary>数据目录降级说明；空字符串表示使用了正常的 %LocalAppData% 位置。</summary>
    public static string LocationWarning { get; private set; } = string.Empty;

    /// <summary>一次性迁移的结果说明；本次启动没有可迁移的数据时为空。</summary>
    public static string MigrationNote { get; private set; } = string.Empty;

    /// <summary>
    /// 接受 --datadir 传入的数据目录。
    /// worker 模式下由主进程传入，避免提权到另一个管理员账户后 %LocalAppData% 指向别人的配置；
    /// 主界面与 dry-run 也支持该参数，用于测试隔离（不影响正常的 %LocalAppData% 路径）。
    /// </summary>
    public static bool ApplyRootOverride(string[] args)
    {
        var value = GetArgument(args, "--datadir");
        if (string.IsNullOrWhiteSpace(value)) return false;

        try
        {
            var full = Path.GetFullPath(value);
            if (!TryPrepare(full)) return false;

            _root = full;
            LocationWarning = string.Empty;   // 位置是否降级由主进程统一提示
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                var value = args[i + 1];
                return value.StartsWith('-') ? null : value;
            }
        }
        return null;
    }

    /// <summary>确保数据目录存在，并在首次调用时执行旧数据迁移。任何失败都不抛异常。</summary>
    public static void Ensure()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogDir);
        }
        catch
        {
            // 目录暂时建不出来也要让程序启动；具体写入失败由各调用点记录，避免启动即崩
        }

        MigrateLegacyOnce();
    }

    /// <summary>
    /// 把旧版放在 exe 同目录的 Data/Logs 迁移到 %LocalAppData%\AutoDefrag，并清理旧目录，
    /// 让 exe 同目录只剩下 exe。逐文件处理：任一文件失败都保留旧文件、不覆盖新数据、不抛异常。
    /// </summary>
    public static void MigrateLegacyOnce()
    {
        lock (MigrationGate)
        {
            if (_migrationDone) return;
            _migrationDone = true;

            if (SameDirectory(Root, ExeDir)) return;   // 已降级到 exe 同目录时不需要迁移

            var migrated = 0;
            var conflicts = 0;
            var failed = 0;
            var notes = new List<string>();

            try
            {
                MigrateDirectory(LegacyDataDir, DataDir, notes, ref migrated, ref conflicts, ref failed);
                MigrateDirectory(LegacyLogDir, LogDir, notes, ref migrated, ref conflicts, ref failed);

                if (conflicts == 0 && failed == 0)
                {
                    TryRemoveEmptyTree(LegacyDataDir);
                    TryRemoveEmptyTree(LegacyLogDir);
                }

                if (migrated > 0 || conflicts > 0 || failed > 0)
                {
                    var text = $"旧版运行数据（exe 同目录 Data/Logs）已迁移到 {Root}：成功 {migrated} 个文件";
                    if (conflicts > 0) text += $"，{conflicts} 个同名文件保留旧位置（新位置已有数据，未覆盖）";
                    if (failed > 0) text += $"，{failed} 个文件迁移失败（旧文件保留）";
                    if (notes.Count > 0) text += "；" + string.Join("；", notes.Take(5)) + (notes.Count > 5 ? "…" : string.Empty);
                    MigrationNote = text;
                }
            }
            catch (Exception ex)
            {
                MigrationNote =
                    $"迁移旧版运行数据失败（旧文件保留在 {ExeDir}，本次使用 {Root}）：{ex.Message}";
            }
        }
    }

    private static void MigrateDirectory(
        string source, string target, List<string> notes,
        ref int migrated, ref int conflicts, ref int failed)
    {
        if (!Directory.Exists(source)) return;

        string[] files;
        try
        {
            files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            failed++;
            notes.Add($"读取旧目录 {source} 失败：{ex.Message}");
            return;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(".write-test.", StringComparison.OrdinalIgnoreCase))
            {
                // 旧版可写性探测的残留：没有价值，直接清掉，否则旧 Data 目录删不掉
                try { File.Delete(file); } catch { /* 删不掉就留着，下次再试 */ }
                continue;
            }

            try
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                var destinationDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDir))
                    Directory.CreateDirectory(destinationDir);

                if (!File.Exists(destination))
                {
                    File.Copy(file, destination, overwrite: false);
                    File.Delete(file);
                    migrated++;
                    continue;
                }

                // 新位置已有同名文件：旧位置更新（例如换了新 exe 后又放回旧配置）则以旧位置为准，
                // 先把新位置那份备份成 .bak 再覆盖，避免任何一方数据被悄悄丢掉。
                var legacyTime = File.GetLastWriteTimeUtc(file);
                var currentTime = File.GetLastWriteTimeUtc(destination);
                if (legacyTime > currentTime.AddSeconds(1))
                {
                    try { File.Copy(destination, destination + ".bak", overwrite: true); } catch { /* 备份失败不阻止迁移 */ }
                    File.Copy(file, destination, overwrite: true);
                    File.Delete(file);
                    migrated++;
                    notes.Add($"{relative} 使用旧位置中较新的版本（原新位置文件已备份为 .bak）");
                    continue;
                }

                // 新位置不旧于旧位置：旧文件是过期副本，清理即可
                File.Delete(file);
                migrated++;
            }
            catch (Exception ex)
            {
                failed++;
                notes.Add($"迁移失败 {name}：{ex.Message}");
            }
        }
    }

    private static void TryRemoveEmptyTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                try { Directory.Delete(sub, false); } catch { /* 非空目录保留 */ }
            }
            Directory.Delete(dir, false);
        }
        catch
        {
            // 旧目录里仍有内容（冲突/失败文件）时保留，用户可在日志里看到说明
        }
    }

    private static string ChooseRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var preferred = Path.Combine(localAppData, FolderName);

        if (TryPrepare(preferred)) return preferred;

        // 降级 1：用户临时目录（仍然不在 exe 同目录，用户把 exe 放桌面也不会多出文件夹）
        var temp = Path.Combine(Path.GetTempPath(), FolderName);
        if (TryPrepare(temp))
        {
            LocationWarning =
                $"无法写入 {preferred}，运行数据暂时保存到 {temp}；" +
                "请检查该目录权限，否则设置与日志可能在系统清理临时文件时丢失";
            return temp;
        }

        // 降级 2：最后的保底位置，保证程序仍然可用（此时 exe 同目录会重新出现 Data/Logs）
        if (TryPrepare(ExeDir))
        {
            LocationWarning =
                $"用户数据目录与临时目录都不可写，运行数据回退到 exe 同目录 {ExeDir}；请检查磁盘是否只读或权限受限";
            return ExeDir;
        }

        LocationWarning =
            $"没有可写的数据目录（已尝试 {preferred}、{temp} 与 exe 同目录），本次运行的设置和日志无法保存";
        return preferred;
    }

    /// <summary>创建目录并真正写一个探测文件，避免“目录能建但文件写不进”的半可写场景。</summary>
    private static bool TryPrepare(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root)) return false;

            var data = Path.Combine(root, "Data");
            var logs = Path.Combine(root, "Logs");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(logs);

            // 探测文件带进程号：主界面与 worker 可能同时启动，固定文件名会互相删掉导致误判
            var probe = Path.Combine(data, $".write-test.{Environment.ProcessId}");
            try
            {
                File.WriteAllText(probe, "ok");
            }
            finally
            {
                try { File.Delete(probe); } catch { /* 清理失败不影响结论 */ }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
