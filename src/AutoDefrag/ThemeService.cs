using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using WpfApplication = System.Windows.Application;

namespace AutoDefrag;

/// <summary>
/// Win11 风格配色：浅色/深色跟随系统或手动指定，强调色取自系统强调色。
/// 所有颜色以 DynamicResource 注入，切换主题时无需重建窗口。
/// </summary>
public static class ThemeService
{
    public static bool IsDark { get; private set; }

    public static void Apply(string mode)
    {
        IsDark = mode == "Dark" || (mode == "System" && SystemPrefersDark());

        var accent = GetSystemAccentColor();

        // 基础色板
        Set("WindowBackground", IsDark ? 0xFF1F1F1F : 0xFFF3F3F3);
        Set("CardBackground", IsDark ? 0xFF292929 : 0xFFFFFFFF);
        Set("CardBorder", IsDark ? 0xFF3A3A3A : 0xFFE3E3E3);
        Set("TextPrimary", IsDark ? 0xFFFFFFFF : 0xFF1A1A1A);
        Set("TextSecondary", IsDark ? 0xFFC6C6C6 : 0xFF5C5C5C);
        Set("TextTertiary", IsDark ? 0xFF9C9C9C : 0xFF8A8A8A);
        Set("ControlBackground", IsDark ? 0xFF2E2E2E : 0xFFFFFFFF);
        Set("ControlBorder", IsDark ? 0xFF464646 : 0xFFD2D2D2);
        Set("ControlHover", IsDark ? 0xFF383838 : 0xFFF2F2F2);
        Set("RowHover", IsDark ? 0xFF323232 : 0xFFF5F8FC);
        Set("CheckBoxFill", IsDark ? 0xFF2B2B2B : 0xFFFFFFFF);
        Set("CheckBoxBorder", IsDark ? 0xFF5C5C5C : 0xFFBEBEBE);
        Set("ToggleTrackOff", IsDark ? 0xFF4A4A4A : 0xFFC5C5C5);
        Set("ToggleThumb", IsDark ? 0xFFF5F5F5 : 0xFFFFFFFF);
        Set("Divider", IsDark ? 0xFF3A3A3A : 0xFFE8E8E8);
        Set("SliderTrack", IsDark ? 0xFF404040 : 0xFFE4E4E4);

        // 强调色 + 派生色
        Set("Accent", accent);
        Set("AccentHover", Blend(accent, IsDark ? Colors.White : Colors.White, 0.14));
        Set("AccentPressed", Blend(accent, Colors.Black, 0.12));
        Set("AccentSoft", Color.FromArgb(0x1F, accent.R, accent.G, accent.B));
        Set("AccentBorder", Color.FromArgb(0x55, accent.R, accent.G, accent.B));
        Set("SliderThumb", accent);
        Set("OnAccent", Luminance(accent) > 0.62 ? Colors.Black : Colors.White);

        Set("Danger", IsDark ? 0xFFFF6B5F : 0xFFC42B1C);
        Set("Success", IsDark ? 0xFF6CCB5F : 0xFF0F7B0F);

        // 进度日志区：深色用介于窗口与卡片之间的表面（不是纯黑），浅色用略偏冷的浅灰；文字着色保证对比度
        Set("LogSurface", IsDark ? 0xFF232323 : 0xFFF7F8FA);
        Set("LogBorder", IsDark ? 0xFF3A3A3A : 0xFFE3E6EA);
        Set("LogText", IsDark ? 0xFFD8D8D8 : 0xFF2B2B2B);
        Set("LogTimestamp", IsDark ? 0xFF8C8C8C : 0xFF8B9099);
        Set("LogInfo", IsDark ? 0xFF79B8FF : 0xFF0B62C4);
        Set("LogWarn", IsDark ? 0xFFE8B339 : 0xFF8A5A00);
        Set("LogError", IsDark ? 0xFFFF8A80 : 0xFFB3261E);
        Set("LogOk", IsDark ? 0xFF6CCB5F : 0xFF0F7B0F);

        foreach (Window window in WpfApplication.Current.Windows)
            ApplyTitleBar(window);

        ThemeChanged?.Invoke();
    }

    /// <summary>主题切换完成（用于需要重建视觉元素的地方，例如富文本日志的着色）。</summary>
    public static event Action? ThemeChanged;

    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var value = IsDark ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+); 19 供更早版本兜底
            Native.DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
            Native.DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
        }
        catch
        {
            // 主题装饰失败不影响功能
        }
    }

    private static void Set(string key, uint argb)
    {
        Set(key, Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }

    private static void Set(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        WpfApplication.Current.Resources[key] = brush;
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color GetSystemAccentColor()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
            if (value is int raw)
            {
                // DWM AccentColor 为 ABGR
                var color = Color.FromArgb(255,
                    (byte)(raw & 0xFF),
                    (byte)((raw >> 8) & 0xFF),
                    (byte)((raw >> 16) & 0xFF));
                if (Luminance(color) > 0.05) return color;
            }
        }
        catch
        {
            // 忽略，使用默认强调色
        }

        return Color.FromRgb(0x00, 0x67, 0xC0);
    }

    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A * (1 - t) + b.A * t),
            (byte)(a.R * (1 - t) + b.R * t),
            (byte)(a.G * (1 - t) + b.G * t),
            (byte)(a.B * (1 - t) + b.B * t));
    }

    private static double Luminance(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
