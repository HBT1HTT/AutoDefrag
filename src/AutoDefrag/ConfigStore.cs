using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoDefrag;

/// <summary>
/// 配置读写。任何字段缺失、损坏、JSON 语法错误都回退到默认值，保证程序永远能起来。
/// 运行数据固定在 %LocalAppData%\AutoDefrag；若旧版放在 exe 同目录且迁移未完成，仍会尝试复用那份 config.json。
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
        var hadFile = File.Exists(AppPaths.ConfigFile);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hadFile)
        {
            try
            {
                var text = System.IO.File.ReadAllText(AppPaths.ConfigFile);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                            present.Add(prop.Name);
                    }

                    var loaded = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions);
                    if (loaded is not null) config = loaded;
                }
            }
            catch (Exception ex)
            {
                note = "config.json 解析失败，已回退默认配置：" + ex.Message;
            }
        }

        // 版本迁移必须依据“原始 JSON 里有没有这个键”，不能靠反序列化后的字段值判断：
        // 旧配置没有新字段时会是类默认值，但用户也可能显式保存过与默认值相同的值。
        if (hadFile && present.Count > 0)
        {
            if (ConfigMigrations.Migrate(config, present, out var migrationNote))
            {
                // 迁移结果立即落盘：避免每次启动都重复迁移，也便于用户/日志确认迁移后的值
                Save(config);
            }

            if (migrationNote is not null)
                note = note is null ? migrationNote : note + "；" + migrationNote;
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

            var legacyStatus = Path.Combine(AppPaths.LegacyDataDir, "drive_status.json");
            if (File.Exists(legacyStatus) && !File.Exists(AppPaths.DriveStatusFile))
                File.Copy(legacyStatus, AppPaths.DriveStatusFile, false);

            var legacyResult = Path.Combine(AppPaths.LegacyDataDir, "last_result.txt");
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

/// <summary>配置版本迁移：v5 → v6 只补新增字段，不覆盖用户已有的任何选择。</summary>
internal static class ConfigMigrations
{
    /// <summary>返回是否发生了迁移（true 时调用方应立即保存）。</summary>
    public static bool Migrate(AppConfig config, IReadOnlySet<string> present, out string? note)
    {
        note = null;

        var version = present.Contains("ConfigVersion") ? config.ConfigVersion : 5;
        if (version > 8)
        {
            note = $"配置 ConfigVersion={version} 高于当前支持的 8，无法识别的字段将被忽略";
            return false;
        }

        var migrated = false;

        if (version < 6)
        {
            // 缺失的新字段按“用户已选档位”的预设补齐；旧配置没有档位时按均衡档。
            var preset = DefragQualityPresets.Get(
                present.Contains("DefragQuality") ? config.DefragQuality : DefragQualityPresets.Balanced);

            if (!present.Contains("DefragQuality")) config.DefragQuality = DefragQualityPresets.Balanced;
            if (!present.Contains("MinFileSizeKB")) config.MinFileSizeKB = preset.MinFileSizeKB;
            if (!present.Contains("MaxFilesPerRun")) config.MaxFilesPerRun = preset.MaxFilesPerRun;
            if (!present.Contains("MaxBytesPerRunGB")) config.MaxBytesPerRunGB = preset.MaxBytesPerRunGB;
            if (!present.Contains("MinReclaimMB")) config.MinReclaimMB = preset.MinReclaimMB;
            if (!present.Contains("VerifyMode")) config.VerifyMode = preset.VerifyMode;
            if (!present.Contains("MaxRounds")) config.MaxRounds = preset.MaxRounds;
            if (!present.Contains("MaxRunMinutes")) config.MaxRunMinutes = preset.MaxRunMinutes;
            if (!present.Contains("SsdOptimizeOnly")) config.SsdOptimizeOnly = true;

            // FragThresholdEnabled / FragThresholdPercent 从 v5 起就存在，这里不做任何改动。
            config.ConfigVersion = 6;
            migrated = true;
            note = $"配置已从 v{version} 迁移到 v6：缺失的整理参数按「{preset.Name}」预设补齐；" +
                   "原有整理方式/碎片率阈值/CPU 阈值保持不变";
        }

        if (config.ConfigVersion < 7)
        {
            // 新增整理完成提示音开关：旧配置缺字段时默认开启，不影响其它设置
            if (!present.Contains("CompletionSound")) config.CompletionSound = true;
            config.ConfigVersion = 7;
            migrated = true;
            var extra = "配置已升级到 v7：新增「整理完成提示音」开关（默认开启）";
            note = note is null ? extra : note + "；" + extra;
        }

        if (config.ConfigVersion < 8)
        {
            // v8：设置窗口精简，整理质量成为“最小文件/单轮上限/可回收下限/校验/轮次/时长”的唯一入口。
            // 旧配置里的数值不丢：命名档位直接套用该档参数；旧“自定义”按数值距离映射到最接近的一档。
            var oldPrefs = config.GetDefragPreferences().Normalized();
            var target = DefragQualityPresets.IsValid(oldPrefs.Quality) && oldPrefs.Quality != DefragQualityPresets.Custom
                ? DefragQualityPresets.Get(oldPrefs.Quality)
                : DefragQualityPresets.Nearest(oldPrefs);

            var mappedFromCustom = oldPrefs.Quality == DefragQualityPresets.Custom ||
                                   !DefragQualityPresets.IsValid(oldPrefs.Quality);
            config.DefragQuality = target.Key;
            config.MinFileSizeKB = target.MinFileSizeKB;
            config.MaxFilesPerRun = target.MaxFilesPerRun;
            config.MaxBytesPerRunGB = target.MaxBytesPerRunGB;
            config.MinReclaimMB = target.MinReclaimMB;
            config.VerifyMode = target.VerifyMode;
            config.MaxRounds = target.MaxRounds;
            config.MaxRunMinutes = target.MaxRunMinutes;

            if (!present.Contains("MergeFreeSpace")) config.MergeFreeSpace = true;

            config.ConfigVersion = 8;
            migrated = true;
            var extra = mappedFromCustom
                ? $"配置已升级到 v8：旧「自定义」参数按最接近的「{target.Name}」档归并" +
                  $"（最小文件 {target.MinFileSizeKB}KB、单轮 {target.MaxFilesPerRun} 文件/{target.MaxBytesPerRunGB}GB、" +
                  $"可回收 {target.MinReclaimMB}MB、{target.MaxRounds} 轮）；文件碎片率阈值与其它设置保持不变"
                : $"配置已升级到 v8：整理参数统一由「{target.Name}」档管理；文件碎片率阈值与其它设置保持不变";
            note = note is null ? extra : note + "；" + extra;
        }

        return migrated;
    }
}
