using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AutoDefrag;

public partial class MainWindow : Window
{
    /// <summary>展开进度详情时额外增加的窗口高度（日志区 148 + 标题/说明/内边距）。</summary>
    private const double ProgressExpandedExtraHeight = 206;

    /// <summary>
    /// 展开态的最小高度：日志框、说明、底部按钮完整可见，磁盘列表仍保留约 120px（至少两行）。
    /// 若工作区本身更矮，会按工作区封顶，此时列表会被压缩，这是物理空间的硬限制。
    /// </summary>
    private const double ProgressExpandedMinHeight = 540;

    private readonly MainViewModel _vm;
    private bool _allowClose;
    private double _expandedAdded;
    private double _heightBeforeExpand;

    /// <summary>展开前的最小高度；大于 0 表示当前处于“已按展开态调整过”的状态。</summary>
    private double _minHeightBeforeExpand;

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

        // 上次退出时是展开状态：启动就恢复，并尽量不压缩磁盘列表（工作区不够时才少加）
        if (_vm.ProgressExpanded) ExpandForProgress();

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

    /// <summary>展开时把窗口加高，避免进度日志挤压磁盘列表；屏幕放不下则只加可用空间。</summary>
    private void ExpandForProgress()
    {
        if (_minHeightBeforeExpand > 0) return;

        var workArea = SystemParameters.WorkArea;
        _heightBeforeExpand = Height;
        _minHeightBeforeExpand = MinHeight;

        // 展开态内容更高：抬高最小高度，避免用户把窗口缩到最小时底部按钮被日志卡挤出可视区；
        // 上限跟随工作区，防止最小高度反而把窗口顶出屏幕。
        MinHeight = Math.Min(ProgressExpandedMinHeight, Math.Max(_minHeightBeforeExpand, workArea.Height - 24));

        var room = Math.Max(0, workArea.Height - 24 - Height);
        var add = Math.Min(ProgressExpandedExtraHeight, room);
        if (add <= 0)
        {
            _expandedAdded = 0;
            return;
        }

        Height += add;
        // WPF 会按设备像素取整窗口尺寸，记录实际增量；收起时直接还原原始高度，避免反复切换累积漂移
        _expandedAdded = Height - _heightBeforeExpand;
    }

    private void CollapseProgress()
    {
        if (_minHeightBeforeExpand <= 0) return;

        // 先恢复最小高度，再还原展开前的高度，避免恢复顺序导致窗口被顶住
        MinHeight = _minHeightBeforeExpand;
        _minHeightBeforeExpand = 0;

        if (_expandedAdded > 0)
        {
            Height = Math.Max(MinHeight, _heightBeforeExpand);
        }

        _expandedAdded = 0;
    }

    private void ProgressToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ProgressExpanded)
        {
            CollapseProgress();
        }
        else
        {
            ExpandForProgress();
        }

        _vm.ToggleProgressExpanded();
        Dispatcher.BeginInvoke(new Action(ScrollLogToEnd));
    }

    private void ProgressLog_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded || _vm is null || !_vm.ProgressExpanded) return;
        ((System.Windows.Controls.TextBox)sender).ScrollToEnd();
    }

    private void ScrollLogToEnd()
    {
        if (_vm.ProgressExpanded) ProgressLogBox.ScrollToEnd();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => App.Instance.ShowSettings();

    private void Optimize_Click(object sender, RoutedEventArgs e) => _vm.StartCleanManual();

    private void Stop_Click(object sender, RoutedEventArgs e) => _vm.StopClean();

    private void OpenLog_Click(object sender, RoutedEventArgs e) => App.Instance.OpenLog();
}
