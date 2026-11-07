using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AutoDefrag;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        // 高缩放（例如 150% 时 1080p 只有 720 逻辑像素）下窗口可能高于工作区，
        // 启动时按工作区收窄，避免底部按钮被屏幕裁掉。
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 24));

        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/icon-256.png"));
        }
        catch
        {
            // 图标加载失败不影响功能
        }

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this);
    }

    /// <summary>由 App 在真正退出时调用，允许窗口关闭。</summary>
    public void PrepareExit() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;

            if (_vm.MinimizeToTray)
            {
                Hide();
                App.Instance.ShowTrayHint();
            }
            else
            {
                App.Instance.ExitApp();
            }
            return;
        }

        base.OnClosing(e);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => App.Instance.ShowSettings();

    private void Optimize_Click(object sender, RoutedEventArgs e) => _vm.StartCleanManual();

    private void OpenLog_Click(object sender, RoutedEventArgs e) => App.Instance.OpenLog();
}
