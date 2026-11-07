using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AutoDefrag;

/// <summary>
/// 独立设置窗口：所有改动先保存在本地草稿里，点“保存”才写回主视图并落盘；
/// 点“取消”或直接关闭窗口不会产生任何修改。
/// </summary>
public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private static readonly Regex ClockRegex =
        new(@"^(?:[01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    private readonly MainViewModel _vm;

    private bool _scheduleEnabled;
    private bool _runAtStartup;
    private bool _autoStart;
    private bool _minimizeToTray;
    private double _cpuThreshold;
    private string _timeText;
    private string _selectedFont;
    private string _themeMode;
    private bool _timeInvalid;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;

        _scheduleEnabled = _vm.ScheduleEnabled;
        _runAtStartup = _vm.RunAtStartup;
        _timeText = _vm.TimeText;
        _autoStart = _vm.AutoStart;
        _minimizeToTray = _vm.MinimizeToTray;
        _cpuThreshold = _vm.CpuThreshold;
        _selectedFont = _vm.SelectedFont;
        _themeMode = _vm.ThemeMode;

        DataContext = this;

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 24));

        switch (_themeMode)
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

    // ---------------------------------------------------------------- 草稿字段

    public IReadOnlyList<string> FontNames => _vm.FontNames;

    public string SelectedFont
    {
        get => _selectedFont;
        set => SetField(ref _selectedFont, value);
    }

    public string SelectedTheme
    {
        get => _themeMode;
        set => SetField(ref _themeMode, value);
    }

    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set => SetField(ref _scheduleEnabled, value);
    }

    public bool RunAtStartup
    {
        get => _runAtStartup;
        set
        {
            if (!SetField(ref _runAtStartup, value)) return;
            OnPropertyChanged(nameof(TimeRowVisibility));
        }
    }

    public Visibility TimeRowVisibility =>
        _runAtStartup ? Visibility.Collapsed : Visibility.Visible;

    public string TimeText
    {
        get => _timeText;
        set
        {
            if (!SetField(ref _timeText, value)) return;
            if (_timeInvalid) SetTimeInvalid(false);
        }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetField(ref _autoStart, value);
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set => SetField(ref _minimizeToTray, value);
    }

    public double CpuThreshold
    {
        get => _cpuThreshold;
        set
        {
            if (!SetField(ref _cpuThreshold, value)) return;
            OnPropertyChanged(nameof(CpuThresholdText));
        }
    }

    public string CpuThresholdText => $"{(int)Math.Round(_cpuThreshold)}%";

    public string NextRunText => _vm.NextRunText;

    public Visibility TimeErrorVisibility =>
        _timeInvalid ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- 操作

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if ((sender as FrameworkElement)?.Tag is string tag)
            SelectedTheme = tag;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_runAtStartup)
        {
            var time = (_timeText ?? string.Empty).Trim();
            if (!ClockRegex.IsMatch(time))
            {
                SetTimeInvalid(true);
                return;
            }
        }

        // 调度：先处理“启动后立即执行”，再写入具体时间，两种切换都不会丢用户输入。
        _vm.ScheduleEnabled = _scheduleEnabled;
        if (_runAtStartup)
        {
            _vm.RunAtStartup = true;
        }
        else
        {
            if (_vm.RunAtStartup) _vm.RunAtStartup = false;
            _vm.TrySetTime((_timeText ?? string.Empty).Trim());
        }

        _vm.CpuThreshold = _cpuThreshold;
        _vm.AutoStart = _autoStart;
        _vm.MinimizeToTray = _minimizeToTray;
        _vm.SelectedFont = _selectedFont;
        _vm.SetTheme(_themeMode);

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void SetTimeInvalid(bool invalid)
    {
        if (_timeInvalid == invalid) return;
        _timeInvalid = invalid;
        OnPropertyChanged(nameof(TimeErrorVisibility));
    }

    // ---------------------------------------------------------------- INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
