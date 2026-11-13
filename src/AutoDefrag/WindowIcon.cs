using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AutoDefrag;

/// <summary>
/// 给窗口设置标题栏/任务栏图标：优先从 exe 内嵌 ICO 里取原生 16/32px 帧（小尺寸不会被 256 缩糊），
/// 取不到时回退到打包的 icon-256.png。必须在 SourceInitialized 之后调用。
/// </summary>
internal static class WindowIcon
{
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW", SetLastError = true)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    public static void Apply(Window window)
    {
        try
        {
            window.Icon = new BitmapImage(new Uri("pack://application:,,,/AutoDefrag;component/Assets/icon-256.png"));
        }
        catch
        {
            // 资源缺失不影响功能
        }

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (ExtractIconEx(AppPaths.ExePath, 0, out var big, out var small, 1) <= 0) return;

            if (small != IntPtr.Zero) SendMessage(hwnd, WmSetIcon, IconSmall, small);
            if (big != IntPtr.Zero) SendMessage(hwnd, WmSetIcon, IconBig, big);

            // 句柄由窗口持有，关闭后再销毁，避免图标在窗口存续期间失效
            window.Closed += (_, _) =>
            {
                if (small != IntPtr.Zero) DestroyIcon(small);
                if (big != IntPtr.Zero) DestroyIcon(big);
            };
        }
        catch
        {
            // 取不到原生图标时保留 WPF 缩放版本
        }
    }
}
