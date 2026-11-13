using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

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
    private bool _completionSound;
    private double _cpuThreshold;
    private string _timeText;
    private string _selectedFont;
    private string _themeMode;
    private string _engineMode;
    private bool _timeInvalid;

    private bool _fragThresholdEnabled;
    private string _fragThresholdText;
    private bool _fragThresholdInvalid;

    // 整理设置草稿：整理质量是“最小文件/单轮上限/可回收下限/校验/轮次/时长”的唯一入口。
    // 文件碎片率阈值、固态只优化、机械盘空闲合并是独立开关，不再与档位互相覆盖。
    private string _quality = DefragQualityPresets.Balanced;
    private bool _ssdOptimizeOnly = true;
    private bool _mergeFreeSpace = true;
    private bool _applyingPreset;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;

        _scheduleEnabled = _vm.ScheduleEnabled;
        _runAtStartup = _vm.RunAtStartup;
        _timeText = _vm.TimeText;
        _autoStart = _vm.AutoStart;
        _minimizeToTray = _vm.MinimizeToTray;
        _completionSound = _vm.CompletionSound;
        _cpuThreshold = _vm.CpuThreshold;
        _selectedFont = _vm.SelectedFont;
        _themeMode = _vm.ThemeMode;
        _engineMode = _vm.EngineMode;
        _fragThresholdEnabled = _vm.FragThresholdEnabled;
        _fragThresholdText = _vm.FragThresholdPercent.ToString();

        var prefs = _vm.DefragPreferences;
        // v8 起整理参数统一由档位管理；旧配置里的“自定义”在迁移时已映射到最接近的一档。
        _quality = DefragQualityPresets.Match(prefs)?.Key
                   ?? (DefragQualityPresets.IsValid(prefs.Quality) && prefs.Quality != DefragQualityPresets.Custom
                       ? prefs.Quality
                       : DefragQualityPresets.Nearest(prefs).Key);
        _ssdOptimizeOnly = prefs.SsdOptimizeOnly;
        _mergeFreeSpace = prefs.MergeFreeSpace;

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

        // 档位在构造时已解析到三档之一；v8 不再提供“自定义”卡片。
        switch (_quality)
        {
            case DefragQualityPresets.Fast:
                QualityFast.IsChecked = true;
                break;
            case DefragQualityPresets.Thorough:
                QualityThorough.IsChecked = true;
                break;
            default:
                QualityBalanced.IsChecked = true;
                break;
        }

        SourceInitialized += (_, _) =>
        {
            ThemeService.ApplyTitleBar(this);
            WindowIcon.Apply(this);
        };
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
            if (_engineMode != "Self")
            {
                // 切到自动/系统整理后参数整组禁用，之前留在输入框里的非法阈值不再阻止保存
                SetFragThresholdInvalid(false);
            }
            OnPropertyChanged(nameof(EngineHint));
            OnPropertyChanged(nameof(DefragSettingsEnabled));
            OnPropertyChanged(nameof(DefragGateHint));
            OnPropertyChanged(nameof(DefragGateVisibility));
            OnPropertyChanged(nameof(DefragGateTitle));
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

    public bool CompletionSound
    {
        get => _completionSound;
        set => SetField(ref _completionSound, value);
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

    // ---------------------------------------------------------------- 整理设置

    /// <summary>只有“本程序整理”时整组可编辑；System / Auto 置灰但保留已保存的值。</summary>
    public bool DefragSettingsEnabled => _engineMode == "Self";

    public Visibility DefragGateVisibility =>
        DefragSettingsEnabled ? Visibility.Collapsed : Visibility.Visible;

    public string DefragGateTitle => _engineMode == "Self"
        ? string.Empty
        : "整理质量暂不可调";

    public string DefragGateHint => _engineMode switch
    {
        "System" => "当前选择「Windows 自带整理」，系统 defrag 不读取这些参数，所以整组不可编辑；已保存的值会保留，切回「本程序整理」后可继续使用。",
        "Auto" => "当前是「自动」：按磁盘存储块大小选择引擎，只有大簇盘会用内置引擎，小簇盘仍走 Windows 自带整理，所以整组不可编辑；已保存的值会保留，点右侧按钮切到「本程序整理」后即可编辑。",
        _ => "可编辑。文件级重写只在大于 4KB 存储块的磁盘上执行，参数在下次整理开始时读取，不影响正在进行的任务。"
    };

    public string QualitySummary => DefragQualityPresets.Get(_quality).Summary;

    /// <summary>固态优化开关独立于三档预设，改动不会影响整理质量。</summary>
    public bool SsdOptimizeOnly
    {
        get => _ssdOptimizeOnly;
        set => SetField(ref _ssdOptimizeOnly, value);
    }

    /// <summary>机械盘空闲空间合并（/X）开关；具体门槛自动判断，不让人手填。</summary>
    public bool MergeFreeSpace
    {
        get => _mergeFreeSpace;
        set => SetField(ref _mergeFreeSpace, value);
    }

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

    private void Quality_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingPreset) return;
        if ((sender as FrameworkElement)?.Tag is not string tag) return;
        if (!DefragQualityPresets.IsValid(tag) || tag == DefragQualityPresets.Custom) return;
        ApplyPreset(tag);
    }

    private void UseSelfEngine_Click(object sender, RoutedEventArgs e)
    {
        // 门控提示里的快捷入口：切换到「本程序整理」后立即可调（仍需点“保存”才落盘）
        EngineSelf.IsChecked = true;
        if (!IsLoaded) EngineMode = "Self";
    }

    /// <summary>套用某一档；数值参数全部来自该档，设置窗口不再单独暴露。</summary>
    private void ApplyPreset(string key)
    {
        if (!DefragQualityPresets.IsValid(key) || key == DefragQualityPresets.Custom)
            return;

        var preset = DefragQualityPresets.Get(key);
        _applyingPreset = true;
        try
        {
            SetQuality(preset.Key);
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    private void SetQuality(string key)
    {
        if (_quality == key) return;
        _quality = key;
        OnPropertyChanged(nameof(QualitySummary));

        _applyingPreset = true;
        try
        {
            QualityFast.IsChecked = key == DefragQualityPresets.Fast;
            QualityBalanced.IsChecked = key == DefragQualityPresets.Balanced;
            QualityThorough.IsChecked = key == DefragQualityPresets.Thorough;
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    /// <summary>把当前草稿转成快照：数值参数完全由选中的档位决定。</summary>
    private DefragPreferences BuildPreferences()
    {
        var preset = DefragQualityPresets.Get(_quality);
        return new DefragPreferences(
            preset.Key,
            preset.MinFileSizeKB,
            preset.MaxFilesPerRun,
            preset.MaxBytesPerRunGB,
            preset.MinReclaimMB,
            preset.VerifyMode,
            preset.MaxRounds,
            preset.MaxRunMinutes,
            _ssdOptimizeOnly,
            _mergeFreeSpace).Normalized();
    }

    // ------------------------------------------------------- 阈值输入框过滤

    /// <summary>数字框只放行 0–9；最大长度由每个输入框的 Tag 指定（碎片率=2、文件数=3、大小=4…）。</summary>
    private void Number_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var box = (TextBox)sender;
        e.Handled = !AcceptsDigitInput(e.Text, box.Text, box.SelectionStart, box.SelectionLength, MaxDigits(box));
    }

    private static int MaxDigits(TextBox box) =>
        int.TryParse(box.Tag?.ToString(), out var max) && max > 0 ? max : 4;

    /// <summary>只接受 ASCII 数字，且输入后总长度不超过 maxDigits。</summary>
    private static bool AcceptsDigitInput(string input, string current, int selectionStart, int selectionLength, int maxDigits)
    {
        if (!IsAsciiDigits(input)) return false;
        var proposed = current.Remove(selectionStart, selectionLength).Insert(selectionStart, input);
        return proposed.Length <= maxDigits;
    }

    /// <summary>空格不会进入数字框；退格、删除、方向键、Home/End、Tab、Ctrl 组合键都保持可用。</summary>
    private void Number_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Space) e.Handled = true;
    }

    /// <summary>粘贴只保留 ASCII 数字；全是字母/符号/中文时不插入内容，且不超过输入框最大长度。</summary>
    private void Number_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        var box = (TextBox)sender;
        var raw = e.DataObject.GetData(System.Windows.DataFormats.UnicodeText) as string
                  ?? e.DataObject.GetData(System.Windows.DataFormats.Text) as string
                  ?? string.Empty;

        var available = MaxDigits(box) - (box.Text.Length - box.SelectionLength);
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

        DefragPreferences defrag;
        if (!DefragSettingsEnabled)
        {
            // 整理参数整组不可编辑（自动/Windows 自带）：沿用已保存的档位与数值，
            // 但“固态只优化/机械盘空闲合并”始终可调，必须照常保存。
            SetFragThresholdInvalid(false);
            defrag = _vm.DefragPreferences with
            {
                SsdOptimizeOnly = _ssdOptimizeOnly,
                MergeFreeSpace = _mergeFreeSpace
            };
        }
        else
        {
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
            else
            {
                // 关闭阈值时保留已保存的百分比，避免用户下次打开时值被重置
                fragPercent = _vm.FragThresholdPercent;
            }

            SetFragThresholdInvalid(false);
            _vm.FragThresholdEnabled = _fragThresholdEnabled;
            _vm.FragThresholdPercent = fragPercent;
            defrag = BuildPreferences();
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
        _vm.CompletionSound = _completionSound;
        _vm.SelectedFont = _selectedFont;
        _vm.SetTheme(_themeMode);
        _vm.EngineMode = _engineMode;
        _vm.ApplyDefragSettings(defrag);

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
