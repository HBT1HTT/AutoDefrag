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

    // 整理设置草稿：三档预设 + P1/P2 参数。改动任意一项都会重新判定档位，不覆盖用户的输入。
    private string _quality = DefragQualityPresets.Balanced;
    private string _minFileSizeText = "32";
    private string _maxFilesText = "80";
    private string _maxBytesText = "16";
    private string _minReclaimText = "4";
    private int _verifyIndex;
    private string _maxRoundsText = "3";
    private string _maxMinutesText = "120";
    private bool _ssdOptimizeOnly = true;
    private bool _defragInvalid;
    private bool _applyingPreset;
    // 用户显式点过「自定义」：未改动参数前保持自定义，即使数值恰好等于某档也不自动认回
    private bool _explicitCustom;

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
        // 保存过“自定义”的配置：即使数值恰好与某档一致，也要如实显示自定义
        _explicitCustom = prefs.Quality == DefragQualityPresets.Custom;
        _quality = _explicitCustom
            ? DefragQualityPresets.Custom
            : DefragQualityPresets.Match(prefs)?.Key ?? DefragQualityPresets.Custom;
        _minFileSizeText = prefs.MinFileSizeKB.ToString();
        _maxFilesText = prefs.MaxFilesPerRun.ToString();
        _maxBytesText = prefs.MaxBytesPerRunGB.ToString();
        _minReclaimText = prefs.MinReclaimMB.ToString();
        _verifyIndex = VerifyIndexFromMode(prefs.VerifyMode);
        _maxRoundsText = prefs.MaxRounds.ToString();
        _maxMinutesText = prefs.MaxRunMinutes.ToString();
        _ssdOptimizeOnly = prefs.SsdOptimizeOnly;

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

        // 档位由当前参数是否与预设完全一致推导：自定义时不选中任何一档。
        switch (_quality)
        {
            case DefragQualityPresets.Fast:
                QualityFast.IsChecked = true;
                break;
            case DefragQualityPresets.Balanced:
                QualityBalanced.IsChecked = true;
                break;
            case DefragQualityPresets.Thorough:
                QualityThorough.IsChecked = true;
                break;
            case DefragQualityPresets.Custom:
                QualityCustom.IsChecked = true;
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
                // 切到自动/系统整理后参数整组禁用，之前留在输入框里的非法值不再阻止保存
                SetFragThresholdInvalid(false);
                SetDefragInvalid(false);
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

    public Visibility QualityCustomVisibility =>
        _quality == DefragQualityPresets.Custom ? Visibility.Visible : Visibility.Collapsed;

    public string QualitySummary => _quality switch
    {
        DefragQualityPresets.Fast =>
            "最快：只处理碎片率 ≥10%、≥256KB、单文件可回收 ≥16MB 的文件；单轮 20 文件/4GB、1 轮、抽样校验。耗时最短，效果一般但不会倒退。",
        DefragQualityPresets.Thorough =>
            "最彻底：≥4KB、单文件可回收 ≥1MB；单轮 300 文件/32GB、最多 8 轮、全量校验。尽量少碎片，耗时最长，建议空闲时使用。",
        DefragQualityPresets.Custom =>
            "自定义：保留当前数值，按下面填写的值执行；改回与某档一致时会自动认回该档，也可直接点上面任一档重新套用。",
        _ =>
            "均衡：默认。≥32KB、单文件可回收 ≥4MB；单轮 80 文件/16GB、最多 3 轮、自动校验（≤2GB 全量，超过抽样）。覆盖与耗时折中。"
    };

    public string MinFileSizeText
    {
        get => _minFileSizeText;
        set
        {
            if (!SetField(ref _minFileSizeText, value)) return;
            if (_defragInvalid) SetDefragInvalid(false);
            MarkDefragEdited();
        }
    }

    public string MaxFilesText
    {
        get => _maxFilesText;
        set
        {
            if (!SetField(ref _maxFilesText, value)) return;
            if (_defragInvalid) SetDefragInvalid(false);
            MarkDefragEdited();
        }
    }

    public string MaxBytesText
    {
        get => _maxBytesText;
        set
        {
            if (!SetField(ref _maxBytesText, value)) return;
            if (_defragInvalid) SetDefragInvalid(false);
            MarkDefragEdited();
        }
    }

    public string MinReclaimText
    {
        get => _minReclaimText;
        set
        {
            if (!SetField(ref _minReclaimText, value)) return;
            if (_defragInvalid) SetDefragInvalid(false);
            MarkDefragEdited();
        }
    }

    public int VerifyIndex
    {
        get => _verifyIndex;
        set
        {
            if (!SetField(ref _verifyIndex, value)) return;
            MarkDefragEdited();
        }
    }

    public string MaxRoundsText
    {
        get => _maxRoundsText;
        set
        {
            if (!SetField(ref _maxRoundsText, value)) return;
            if (_defragInvalid) SetDefragInvalid(false);
            MarkDefragEdited();
        }
    }

    public string MaxMinutesText
    {
        get => _maxMinutesText;
        set
        {
            if (!SetField(ref _maxMinutesText, value)) return;
            if (_defragInvalid) SetDefragInvalid(false);
            MarkDefragEdited();
        }
    }

    /// <summary>固态优化开关独立于三档预设，改动不会把档位变成自定义。</summary>
    public bool SsdOptimizeOnly
    {
        get => _ssdOptimizeOnly;
        set => SetField(ref _ssdOptimizeOnly, value);
    }

    public Visibility DefragErrorVisibility =>
        _defragInvalid ? Visibility.Visible : Visibility.Collapsed;

    public bool FragThresholdEnabled
    {
        get => _fragThresholdEnabled;
        set
        {
            if (!SetField(ref _fragThresholdEnabled, value)) return;
            OnPropertyChanged(nameof(FragThresholdVisibility));
            MarkDefragEdited();
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
            MarkDefragEdited();
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

        if (tag == DefragQualityPresets.Custom)
        {
            // 选择“自定义”只解锁（本组在 Self 下本就全可编辑），不重置现有数值
            _explicitCustom = true;
            SetQuality(DefragQualityPresets.Custom);
            return;
        }

        _explicitCustom = false;
        ApplyPreset(tag);
    }

    private void UseSelfEngine_Click(object sender, RoutedEventArgs e)
    {
        // 门控提示里的快捷入口：切换到「本程序整理」后立即可调（仍需点“保存”才落盘）
        EngineSelf.IsChecked = true;
        if (!IsLoaded) EngineMode = "Self";
    }

    /// <summary>整组套用某一档参数；不会改动“固态只优化”（它独立于三档）。</summary>
    private void ApplyPreset(string key)
    {
        if (!DefragQualityPresets.IsValid(key) || key == DefragQualityPresets.Custom)
            return;

        var preset = DefragQualityPresets.Get(key);
        _explicitCustom = false;
        _applyingPreset = true;
        try
        {
            MinFileSizeText = preset.MinFileSizeKB.ToString();
            MaxFilesText = preset.MaxFilesPerRun.ToString();
            MaxBytesText = preset.MaxBytesPerRunGB.ToString();
            MinReclaimText = preset.MinReclaimMB.ToString();
            VerifyIndex = VerifyIndexFromMode(preset.VerifyMode);
            MaxRoundsText = preset.MaxRounds.ToString();
            MaxMinutesText = preset.MaxRunMinutes.ToString();
            FragThresholdEnabled = preset.FragThresholdEnabled;
            FragThresholdText = preset.FragThresholdPercent.ToString();
            SetQuality(preset.Key);
        }
        finally
        {
            _applyingPreset = false;
        }

        SetFragThresholdInvalid(false);
        SetDefragInvalid(false);
    }

    /// <summary>手动改动任意一项后重新判定档位；与某档完全一致就显示该档，否则显示“自定义”。</summary>
    private void MarkDefragEdited()
    {
        if (_applyingPreset) return;
        // 显式“自定义”只在用户真正改动参数后才参与自动认档
        _explicitCustom = false;
        SetQuality(DefragQualityPresets.Match(BuildPreferences())?.Key ?? DefragQualityPresets.Custom);
    }

    private void SetQuality(string key)
    {
        if (_quality == key) return;
        _quality = key;
        OnPropertyChanged(nameof(QualitySummary));
        OnPropertyChanged(nameof(QualityCustomVisibility));

        // 自定义时三个档位都不选中；参数与某档完全一致时把该档重新选中，
        // 避免“把数值改回预设后摘要显示某档、但没有任何单选被选中”的状态。
        _applyingPreset = true;
        try
        {
            if (key == DefragQualityPresets.Custom)
            {
                QualityFast.IsChecked = false;
                QualityBalanced.IsChecked = false;
                QualityThorough.IsChecked = false;
                QualityCustom.IsChecked = true;
            }
            else
            {
                QualityFast.IsChecked = key == DefragQualityPresets.Fast;
                QualityBalanced.IsChecked = key == DefragQualityPresets.Balanced;
                QualityThorough.IsChecked = key == DefragQualityPresets.Thorough;
                QualityCustom.IsChecked = false;
            }
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    /// <summary>把当前草稿转成快照；非法/空输入先按默认值代入，仅用于判定档位。</summary>
    private DefragPreferences BuildPreferences()
    {
        return new DefragPreferences(
            _quality,
            _fragThresholdEnabled,
            ParseOrDefault(_fragThresholdText, 10),
            ParseOrDefault(_minFileSizeText, 32),
            ParseOrDefault(_maxFilesText, 80),
            ParseOrDefault(_maxBytesText, 16),
            ParseOrDefault(_minReclaimText, 4),
            VerifyModeFromIndex(_verifyIndex),
            ParseOrDefault(_maxRoundsText, 3),
            ParseOrDefault(_maxMinutesText, 120),
            _ssdOptimizeOnly).Normalized();
    }

    private static int ParseOrDefault(string? text, int fallback) =>
        int.TryParse((text ?? string.Empty).Trim(), out var value) ? value : fallback;

    private static int VerifyIndexFromMode(string? mode) => mode switch
    {
        "Full" => 1,
        "Sample" => 2,
        _ => 0
    };

    private static string VerifyModeFromIndex(int index) => index switch
    {
        1 => "Full",
        2 => "Sample",
        _ => "Auto"
    };

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
            // 整理参数整组不可编辑（自动/Windows 自带）：沿用已保存的值，
            // 不能因为禁用面板里残留的旧输入把“保存”卡住。
            SetFragThresholdInvalid(false);
            SetDefragInvalid(false);
            defrag = _vm.DefragPreferences;
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

            var minFile = ParseOrDefault(_minFileSizeText, -1);
            var maxFiles = ParseOrDefault(_maxFilesText, -1);
            var maxBytes = ParseOrDefault(_maxBytesText, -1);
            var minReclaim = ParseOrDefault(_minReclaimText, -1);
            var maxRounds = ParseOrDefault(_maxRoundsText, -1);
            var maxMinutes = ParseOrDefault(_maxMinutesText, -1);
            if (minFile is < 4 or > 4096 || maxFiles is < 10 or > 500 || maxBytes is < 1 or > 64 ||
                minReclaim is < 0 or > 64 || maxRounds is < 1 or > 10 || maxMinutes is < 0 or > 1440)
            {
                SetDefragInvalid(true);
                return;
            }

            SetDefragInvalid(false);
            defrag = new DefragPreferences(
                _quality, _fragThresholdEnabled, fragPercent,
                minFile, maxFiles, maxBytes, minReclaim,
                VerifyModeFromIndex(_verifyIndex), maxRounds, maxMinutes, _ssdOptimizeOnly).Normalized();
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

    private void SetDefragInvalid(bool invalid)
    {
        if (_defragInvalid == invalid) return;
        _defragInvalid = invalid;
        OnPropertyChanged(nameof(DefragErrorVisibility));
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
