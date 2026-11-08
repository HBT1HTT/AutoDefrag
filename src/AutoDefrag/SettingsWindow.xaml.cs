using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
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
    private string _engineMode;
    private bool _timeInvalid;

    private bool _fragThresholdEnabled;
    private string _fragThresholdText;
    private bool _fragThresholdInvalid;

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
        _engineMode = _vm.EngineMode;
        _fragThresholdEnabled = _vm.FragThresholdEnabled;
        _fragThresholdText = _vm.FragThresholdPercent.ToString();

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

        switch (_engineMode)
        {
            case "System":
                EngineSystem.IsChecked = true;
                break;
            case "Auto":
                EngineAuto.IsChecked = true;
                break;
            default:
                EngineSelf.IsChecked = true;
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

    public string EngineMode
    {
        get => _engineMode;
        set
        {
            if (!SetField(ref _engineMode, value)) return;
            OnPropertyChanged(nameof(EngineHint));
        }
    }

    public string EngineHint => _engineMode switch
    {
        "System" => "调用 Windows 自带的整理工具，和旧版一致。对每块大于 4KB 的磁盘（例如 64KB、128KB）无效，会提示跳过，不会假装成功。",
        "Auto" => "默认选择。按每块盘的存储块大小自动选：大于 4KB 用本程序整理，不超过 4KB 用 Windows 自带整理。已有的旧配置保存过的选择不会被改动。",
        _ => "大容量存储块磁盘由本程序逐个重写文件来整理，可以真的减少碎片；小存储块磁盘仍用系统整理，更稳更快。重写会把文件完整读写一遍，耗时约等于复制该文件。"
    };

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

    // ---------------------------------------------------------------- 整理范围

    public bool FragThresholdEnabled
    {
        get => _fragThresholdEnabled;
        set
        {
            if (!SetField(ref _fragThresholdEnabled, value)) return;
            OnPropertyChanged(nameof(FragThresholdVisibility));
        }
    }

    public Visibility FragThresholdVisibility =>
        _fragThresholdEnabled ? Visibility.Visible : Visibility.Collapsed;

    public string FragThresholdText
    {
        get => _fragThresholdText;
        set
        {
            if (!SetField(ref _fragThresholdText, value)) return;
            if (_fragThresholdInvalid) SetFragThresholdInvalid(false);
        }
    }

    public Visibility FragThresholdErrorVisibility =>
        _fragThresholdInvalid ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- 操作

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if ((sender as FrameworkElement)?.Tag is string tag)
            SelectedTheme = tag;
    }

    private void Engine_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if ((sender as FrameworkElement)?.Tag is string tag)
            EngineMode = tag;
    }

    // ------------------------------------------------------- 阈值输入框过滤

    /// <summary>
    /// 输入阶段只放行 0–9 的单个字符，并且保证输入后总长度不超过 2 位（最大值 15）；
    /// 0、16 这类两位以内的数字可以先显示出来，保存时再按 1–15 拒绝并提示。
    /// </summary>
    private void FragThreshold_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        e.Handled = !AcceptsDigitInput(e.Text, box.Text, box.SelectionStart, box.SelectionLength);
    }

    /// <summary>只接受 ASCII 数字，且输入后总长度不超过 2 位（最大值 15）。</summary>
    private static bool AcceptsDigitInput(string input, string current, int selectionStart, int selectionLength)
    {
        if (!IsAsciiDigits(input)) return false;
        var proposed = current.Remove(selectionStart, selectionLength).Insert(selectionStart, input);
        return proposed.Length <= 2;
    }

    /// <summary>空格不会进入数字框；退格、删除、方向键、Home/End、Tab、Ctrl 组合键都保持可用。</summary>
    private void FragThreshold_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
    }

    /// <summary>粘贴只保留 ASCII 数字；全是字母/符号/中文时不插入内容，且最多保留 2 位。</summary>
    private void FragThreshold_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        var box = (TextBox)sender;
        var raw = e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) as string
                  ?? e.DataObject.GetData(System.Windows.DataFormats.Text) as string
                  ?? string.Empty;

        var available = 2 - (box.Text.Length - box.SelectionLength);
        var digits = FilterPastedDigits(raw, available);
        if (digits.Length == 0)
        {
            // 纯字母/符号/中文的粘贴直接忽略，等于没有插入
            e.CancelCommand();
            return;
        }

        e.DataObject = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, digits);
    }

    /// <summary>粘贴内容只保留 ASCII 数字，最多 available 位；没有数字时返回空。</summary>
    private static string FilterPastedDigits(string raw, int available)
    {
        if (available <= 0) return string.Empty;
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        return digits.Length > available ? digits[..available] : digits;
    }

    private static bool IsAsciiDigits(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (c is < '0' or > '9') return false;
        }
        return true;
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

        var fragPercent = 0;
        if (_fragThresholdEnabled)
        {
            fragPercent = ParseFragThreshold(_fragThresholdText);
            if (fragPercent is < 1 or > 15)
            {
                // 超出范围直接拒绝保存并提示，不静默截断
                SetFragThresholdInvalid(true);
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
        _vm.EngineMode = _engineMode;
        _vm.FragThresholdEnabled = _fragThresholdEnabled;
        if (_fragThresholdEnabled) _vm.FragThresholdPercent = fragPercent;

        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void SetTimeInvalid(bool invalid)
    {
        if (_timeInvalid == invalid) return;
        _timeInvalid = invalid;
        OnPropertyChanged(nameof(TimeErrorVisibility));
    }

    private void SetFragThresholdInvalid(bool invalid)
    {
        if (_fragThresholdInvalid == invalid) return;
        _fragThresholdInvalid = invalid;
        OnPropertyChanged(nameof(FragThresholdErrorVisibility));
    }

    /// <summary>解析阈值输入；非数字或越界时返回 0，由调用方判定为无效。</summary>
    private static int ParseFragThreshold(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim().TrimEnd('%', '％').Trim();
        if (!int.TryParse(trimmed, out var value)) return 0;
        return value;
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
