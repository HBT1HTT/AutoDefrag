using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

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
        // 启动时按工作区收窄，避免底部按钮被屏幕裁掉；内容区本身可滚动。
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
        Loaded += (_, _) => SyncThemeRadio();
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

    private void SyncThemeRadio()
    {
        switch (_vm.ThemeMode)
        {
            case "Light":
                ThemeLight.IsChecked = true;
                break;
            case "Dark":
                ThemeDark.IsChecked = true;
                break;
            default:
                ThemeSystem.IsChecked = true;
                break;
        }
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if ((sender as FrameworkElement)?.Tag is string tag)
            _vm.SetTheme(tag);
    }

    private void CommitTime()
    {
        if (!_vm.TrySetTime(TimeBox.Text))
            TimeBox.Text = _vm.TimeText;
    }

    private void TimeBox_LostFocus(object sender, RoutedEventArgs e) => CommitTime();

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTime();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TimeBox.Text = _vm.TimeText;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => _vm.ToggleSelectAll();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _vm.RefreshDisks();

    private void Optimize_Click(object sender, RoutedEventArgs e) => _vm.StartCleanManual();

    private void Probe_Click(object sender, RoutedEventArgs e) => _vm.StartProbe();

    private void OpenLog_Click(object sender, RoutedEventArgs e) => App.Instance.OpenLog();
}
