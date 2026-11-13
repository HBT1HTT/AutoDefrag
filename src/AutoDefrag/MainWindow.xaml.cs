using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;

namespace AutoDefrag;

public partial class MainWindow : Window
{
    /// <summary>日志行首的 `HH:mm:ss`，渲染时弱化显示；不改变日志文本本身。</summary>
    private static readonly Regex LogStampPattern = new(@"^\d{2}:\d{2}:\d{2}", RegexOptions.Compiled);

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

    /// <summary>上次渲染到富文本里的日志，避免同内容重复重建。</summary>
    private string _renderedLog = "\u0000";

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

        SourceInitialized += (_, _) =>
        {
            ThemeService.ApplyTitleBar(this);
            WindowIcon.Apply(this);
        };

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        ThemeService.ThemeChanged += OnThemeChanged;
        Loaded += OnLoadedForLog;
        Closed += OnClosedForLog;
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

    private void OnLoadedForLog(object sender, RoutedEventArgs e) => RenderProgressLog(_vm.ProgressLogText, true);

    private void OnClosedForLog(object? sender, EventArgs e)
    {
        // 窗口与 ViewModel 生命周期基本一致，仍显式解绑，避免再次创建窗口时重复订阅
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        ThemeService.ThemeChanged -= OnThemeChanged;
        Loaded -= OnLoadedForLog;
        Closed -= OnClosedForLog;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ProgressLogText))
            RenderProgressLog(_vm.ProgressLogText, false);
    }

    private void OnThemeChanged()
    {
        // DynamicResource 会自动换色，但 RichTextBox 里 Run 的前景色是快照，主题切换后需按新主题重建
        Dispatcher.BeginInvoke(new Action(() => RenderProgressLog(_vm.ProgressLogText, true)), DispatcherPriority.Background);
    }

    /// <summary>
    /// 把日志文本渲染成富文本：时间戳弱化、警告/错误/暂停/恢复按语义着色。
    /// 只影响显示，<see cref="MainViewModel.ProgressLogText"/> 与日志文件内容保持原样。
    /// </summary>
    private void RenderProgressLog(string text, bool force)
    {
        text ??= string.Empty;
        if (!force && string.Equals(text, _renderedLog, StringComparison.Ordinal)) return;
        _renderedLog = text;

        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = ProgressLogBox.FontFamily,
            FontSize = ProgressLogBox.FontSize,
            TextAlignment = TextAlignment.Left
        };

        var lines = text.Length == 0 ? Array.Empty<string>() : text.Split('\n');
        if (lines.Length == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("（暂无运行日志）"))
            {
                Margin = new Thickness(0),
                Foreground = LogBrush("LogTimestamp")
            });
        }
        else
        {
            var widest = 0d;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');
                document.Blocks.Add(BuildLogParagraph(line));
                if (line.Length > 0) widest = Math.Max(widest, MeasureLogWidth(line));
            }

            // 不换行：按最长一行固定文档宽度，短内容不出现横向滚动条，超长内容可横向滚动
            document.PageWidth = Math.Min(6000, Math.Max(200, widest + 28));
        }

        ProgressLogBox.Document = document;
        Dispatcher.BeginInvoke(new Action(ScrollLogToEnd), DispatcherPriority.Loaded);
    }

    private Paragraph BuildLogParagraph(string line)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var rest = line;

        if (LogStampPattern.IsMatch(line))
        {
            paragraph.Inlines.Add(new Run(line[..8]) { Foreground = LogBrush("LogTimestamp") });
            rest = line.Length > 8 ? line[8..] : string.Empty;
        }

        paragraph.Inlines.Add(new Run(rest) { Foreground = PickLogBrush(rest) });
        return paragraph;
    }

    private Brush PickLogBrush(string message)
    {
        if (message.Contains("错误") || message.Contains("失败") || message.Contains("异常"))
            return LogBrush("LogError");
        if (message.Contains("警告") || message.Contains("跳过") || message.Contains("不足") || message.Contains("无法"))
            return LogBrush("LogWarn");
        if (message.Contains("暂停") || message.Contains("恢复") || message.Contains("继续"))
            return LogBrush("LogInfo");
        if (message.Contains("完成") || message.Contains("成功") || message.Contains("就绪"))
            return LogBrush("LogOk");
        return LogBrush("LogText");
    }

    private Brush LogBrush(string key) => (Brush)FindResource(key);

    private double MeasureLogWidth(string line)
    {
        var typeface = new Typeface(ProgressLogBox.FontFamily, FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(line, CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight, typeface, ProgressLogBox.FontSize,
            System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
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
