using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoDefrag;

/// <summary>
/// 配置读写。任何字段缺失、损坏、JSON 语法错误都回退到默认值，保证程序永远能起来。
/// 首次运行时会尝试复用旧脚本的 Data\config.json（当数据目录退回到 %LocalAppData% 时）。
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>读取配置；note 用于把迁移/回退情况写进日志。</summary>
    public AppConfig Load(out string? note)
    {
        note = null;
        AppPaths.Ensure();

        if (!File.Exists(AppPaths.ConfigFile) &&
            !string.Equals(AppPaths.LegacyConfigFile, AppPaths.ConfigFile, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(AppPaths.LegacyConfigFile))
        {
            TryMigrateLegacy(out note);
        }

        var config = new AppConfig();
        if (File.Exists(AppPaths.ConfigFile))
        {
            try
            {
                var text = File.ReadAllText(AppPaths.ConfigFile);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var loaded = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions);
                    if (loaded is not null) config = loaded;
                }
            }
            catch (Exception ex)
            {
                note = "config.json 解析失败，已回退默认配置：" + ex.Message;
            }
        }

        config.Normalize();
        return config;
    }

    public void Save(AppConfig config)
    {
        try
        {
            config.Normalize();
            AppPaths.Ensure();

            var json = JsonSerializer.Serialize(config, JsonOptions);
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            File.Move(tmp, AppPaths.ConfigFile, true);
        }
        catch (Exception ex)
        {
            Logger.Error("保存 config.json 失败: " + ex.Message);
        }
    }

    private static void TryMigrateLegacy(out string? note)
    {
        note = null;
        try
        {
            File.Copy(AppPaths.LegacyConfigFile, AppPaths.ConfigFile, false);

            // 旧脚本"每日一次锁"也一并带过来，避免当天重复跑一遍
            var legacyLastRun = AppPaths.LegacyLastRunFile;
            if (File.Exists(legacyLastRun) && !File.Exists(AppPaths.LastRunFile))
                File.Copy(legacyLastRun, AppPaths.LastRunFile, false);

            var legacyStatus = Path.Combine(AppPaths.ExeDir, "Data", "drive_status.json");
            if (File.Exists(legacyStatus) && !File.Exists(AppPaths.DriveStatusFile))
                File.Copy(legacyStatus, AppPaths.DriveStatusFile, false);

            var legacyResult = Path.Combine(AppPaths.ExeDir, "Data", "last_result.txt");
            if (File.Exists(legacyResult) && !File.Exists(AppPaths.LastResultFile))
                File.Copy(legacyResult, AppPaths.LastResultFile, false);

            note = "已复用旧脚本配置: " + AppPaths.LegacyConfigFile;
        }
        catch (Exception ex)
        {
            note = "复用旧配置失败，改用默认值：" + ex.Message;
        }
    }
}
