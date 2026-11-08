using Microsoft.Win32;

namespace AutoDefrag;

/// <summary>
/// 开机自启：HKCU\Software\Microsoft\Windows\CurrentVersion\Run，值名 AutoDefrag。
/// 值为 "exe 完整路径" --tray，路径在运行时推导，不写死。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoDefrag";

    public static string ExpectedValue => $"\"{AppPaths.ExePath}\" --tray";

    /// <summary>把注册表同步为配置里的期望状态（配置是唯一真相来源）。</summary>
    public static bool Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            var current = key.GetValue(ValueName) as string;
            if (enabled)
            {
                if (!string.Equals(current, ExpectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, ExpectedValue, RegistryValueKind.String);
                    Logger.Info($"已开启开机自启 -> {ValueName}={ExpectedValue}");
                }
            }
            else if (current is not null)
            {
                // 只清理指向本副本的旧值。Run 键是整机共享的，若指向其他目录的 AutoDefrag
                // （另一份便携版/已安装版），删除会误关别人的开机自启。
                if (string.Equals(current, ExpectedValue, StringComparison.OrdinalIgnoreCase) ||
                    current.StartsWith($"\"{AppPaths.ExePath}\"", StringComparison.OrdinalIgnoreCase) ||
                    current.StartsWith(AppPaths.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    Logger.Info("已关闭开机自启（已移除指向本程序的 Run 键）");
                }
                else
                {
                    Logger.Info($"Run 键中的开机自启项 AutoDefrag 指向其他副本（{current}），未改动；如需关闭请在该副本的设置里操作");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("同步开机自启失败: " + ex.Message);
            return false;
        }
    }
}
