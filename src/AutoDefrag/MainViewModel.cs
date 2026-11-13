using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace AutoDefrag;

public enum NotifyLevel { Info, Warning, Error }

/// <summary>
/// 主界面 ViewModel：勾选磁盘、调度开关、设置项都即时落盘；
/// 实际整理交给提权 worker，本类只负责触发、轮询进度与展示结果。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly Regex ClockRegex = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    private readonly AppConfig _config;
    private readonly ConfigStore _store;
    private readonly WorkerService _worker = new();
    private readonly DispatcherTimer _watchTimer = new();
    private readonly DispatcherTimer _scheduleTimer = new();

    private string _lastClock;
    private string? _triggeredDate;
    private bool _suppressSave;
    private bool _isBusy;
    private bool _isTimeInvalid;
    private string _statusText = "空闲";
    private string _footerHint = string.Empty;
    private string _lastResultText = "暂无执行记录";
    private readonly IReadOnlyList<string> _fontNames;
    private string _selectedFont;
    private string _largeClusterHint = string.Empty;
    private bool _locationWarningShown;

    // 主界面底部进度区
    private bool _previewing;
    private bool _stopping;
    private int _progressPercent = -1;
    private string _progressTitle = "就绪";
    private string _progressDetail = string.Empty;
    private string _progressPercentText = "就绪";
    private string _progressLogText = string.Empty;
    private readonly ObservableCollection<string> _progressLines = new();
    private long _progressOffset;
    private const int ProgressLogMaxLines = 200;
    private bool _completionSoundPlayed;

    public MainViewModel(AppConfig config, ConfigStore store)
    {
        _config = config;
        _store = store;
        _lastClock = config.RunAtStartup ? "03:00" : config.DailyTime;

        _fontNames = FontService.Names;
        var savedFont = (config.UiFont ?? string.Empty).Trim();
        if (savedFont.Length > 0 && FontService.IsInstalled(savedFont))
        {
            _selectedFont = savedFont;
        }
        else
        {
            _selectedFont = FontService.ResolveRecommended();
            if (savedFont.Length > 0)
                Logger.Warn($"配置的界面字体不可用: {savedFont}，已回退到 {_selectedFont}");
        }

        _watchTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _watchTimer.Tick += (_, _) => OnWatchTick();

        _scheduleTimer.Interval = TimeSpan.FromSeconds(30);
        _scheduleTimer.Tick += (_, _) => OnScheduleTick();
    }

    public ObservableCollection<DiskItem> Disks { get; } = new();

    /// <summary>系统已安装字体族名，供设置里的字体下拉使用。</summary>
    public IReadOnlyList<string> FontNames => _fontNames;

    /// <summary>当前界面字体；切换后立即应用并写入 config.json 的 UiFont。</summary>
    public string SelectedFont
    {
        get => _selectedFont;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (string.Equals(_selectedFont, value, StringComparison.OrdinalIgnoreCase)) return;

            _selectedFont = value;
            _config.UiFont = value;
            _store.Save(_config);
            FontService.Apply(value);
            Logger.Info("界面字体已切换: " + value);
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string, string, NotifyLevel>? NotificationRequested;

    public int NotificationTimeout => _config.NotificationTimeout;

    // ---------------------------------------------------------------- 启动与调度

    public void Start()
    {
        RefreshDisks();
        RefreshLastResult();
        _watchTimer.Start();
        _scheduleTimer.Start();

        var decision = GetDailyDecision();
        Logger.Info($"开机自动整理判定: {decision} (DailyTime={_config.DailyTime}, ScheduleEnabled={_config.ScheduleEnabled})");

        if (decision == "done")
        {
            NotificationRequested?.Invoke("AutoDefrag", "今天已经整理过了，不用重复整理", NotifyLevel.Info);
        }
        else if (decision == "run")
        {
            _triggeredDate = Today;
            StartClean(auto: true);
        }

        OnPropertyChanged(nameof(NextRunText));
    }

    private void OnScheduleTick()
    {
        OnPropertyChanged(nameof(NextRunText));

        if (_worker.IsRunning || _isBusy || _previewing) return;
        if (GetDailyDecision() != "run") return;

        _triggeredDate = Today;   // 先记下本会话今日已触发，避免 30s 反复重试
        Logger.Info("每日自动整理已触发: DailyTime=" + _config.DailyTime);
        StartClean(auto: true);
    }

    private string GetDailyDecision()
    {
        if (!_config.ScheduleEnabled) return "wait";
        if (LastRunIsToday()) return "done";
        if (_triggeredDate == Today) return "done";
        if (_config.RunAtStartup) return "run";
        if (DateTime.Now >= TargetTimeToday()) return "run";
        return "wait";
    }

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    private DateTime TargetTimeToday()
    {
        var parts = _config.DailyTime.Split(':');
        var hour = int.TryParse(parts[0], out var h) ? h : 0;
        var minute = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        return DateTime.Today.AddHours(hour).AddMinutes(minute);
    }

    private bool LastRunIsToday()
    {
        try
        {
            if (!File.Exists(AppPaths.LastRunFile)) return false;
            return File.ReadAllText(AppPaths.LastRunFile).Trim() == Today;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 磁盘列表

    public void RefreshDisks()
    {
        var firstRun = _config.SelectedDrives is null;

        _suppressSave = true;
        Disks.Clear();
        foreach (var drive in DiskService.GetFixedDrives())
        {
            var letter = DiskService.Letter(drive);
            var selected = _config.SelectedDrives is null ||
                           _config.SelectedDrives.Any(x => string.Equals(x, letter, StringComparison.OrdinalIgnoreCase));

            var clusterBytes = Native.GetClusterSize(letter);
            string? toolTip = null;
            if (DiskService.IsLargeCluster(clusterBytes))
            {
                var effective = EngineResolver.Effective(_config.EngineMode, clusterBytes);
                var ssdOptimize = _config.SsdOptimizeOnly && DiskService.GetMediaKind(drive) == DiskMediaKind.Ssd;
                toolTip = effective == "System"
                    ? $"这块磁盘的存储块比较大（每块 {DiskService.ClusterText(clusterBytes)}），Windows 自带的整理对它无效，选中后会被跳过并提示原因。"
                    : ssdOptimize
                        ? $"这块磁盘的存储块比较大（每块 {DiskService.ClusterText(clusterBytes)}），且是固态硬盘：默认只做系统优化，不重写文件（可在设置里关闭）。"
                        : $"这块磁盘的存储块比较大（每块 {DiskService.ClusterText(clusterBytes)}），会用本程序重写文件来整理，逐个处理、可随时停止。";
            }

            Disks.Add(new DiskItem(
                letter,
                DiskService.VolumeLabel(drive),
                DiskService.MediaText(drive),
                DiskService.DetailText(drive, clusterBytes),
                DiskService.AvailableText(drive),
                DiskService.TotalText(drive),
                selected,
                toolTip,
                OnDiskSelectionChanged));
        }

        if (firstRun)
        {
            _config.SelectedDrives = Disks.Where(d => d.IsSelected).Select(d => d.Letter).ToList();
            _store.Save(_config);
            Logger.Info("首次运行：默认勾选全部本地固定磁盘 (" + string.Join(",", _config.SelectedDrives) + ")");
        }
        _suppressSave = false;

        UpdateLargeClusterHint();

        OnPropertyChanged(nameof(HasDisks));
        OnPropertyChanged(nameof(EmptyHintVisibility));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(SelectAllText));
        RaiseSelectionSummary();
    }

    /// <summary>用通俗说法提示“这块磁盘走了哪条整理路径”。</summary>
    private void UpdateLargeClusterHint()
    {
        var notes = new List<string>();
        foreach (var disk in Disks)
        {
            var clusterBytes = Native.GetClusterSize(disk.Letter);
            if (!DiskService.IsLargeCluster(clusterBytes)) continue;

            var effective = EngineResolver.Effective(_config.EngineMode, clusterBytes);
            var auto = _config.EngineMode == EngineResolver.Auto ? "自动判断为" : string.Empty;
            var info = DiskService.GetFixedDrives()
                .FirstOrDefault(d => string.Equals(DiskService.Letter(d), disk.Letter, StringComparison.OrdinalIgnoreCase));
            var ssdOptimize = _config.SsdOptimizeOnly && info is not null &&
                              DiskService.GetMediaKind(info) == DiskMediaKind.Ssd;
            notes.Add(effective == "System"
                ? $"{disk.Letter} 的存储块较大（每块 {DiskService.ClusterText(clusterBytes)}），{auto}Windows 自带整理对它无效，会自动跳过并写日志"
                : ssdOptimize
                    ? $"{disk.Letter} 的存储块较大（每块 {DiskService.ClusterText(clusterBytes)}），{auto}固态硬盘默认只做系统优化、不重写文件"
                    : $"{disk.Letter} 的存储块较大（每块 {DiskService.ClusterText(clusterBytes)}），{auto}用本程序重写文件来整理");
        }

        _largeClusterHint = notes.Count > 0 ? string.Join("；", notes) + "。" : string.Empty;
        OnPropertyChanged(nameof(LargeClusterHint));
        OnPropertyChanged(nameof(LargeClusterHintVisibility));
    }

    public bool HasDisks => Disks.Count > 0;
    public Visibility EmptyHintVisibility => Disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>超大簇（&gt;4KB）卷的简短提示，正常情况下隐藏。</summary>
    public string LargeClusterHint => _largeClusterHint;

    public Visibility LargeClusterHintVisibility =>
        _largeClusterHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public int SelectedCount => Disks.Count(d => d.IsSelected);

    public bool CanStart => !_isBusy && !_previewing && SelectedCount > 0;

    public string SelectAllText =>
        Disks.Count > 0 && Disks.All(d => d.IsSelected) ? "取消全选" : "全选";

    public void ToggleSelectAll()
    {
        if (Disks.Count == 0) return;

        var selectAll = !Disks.All(d => d.IsSelected);
        _suppressSave = true;
        foreach (var disk in Disks) disk.IsSelected = selectAll;
        _suppressSave = false;

        _config.SelectedDrives = Disks.Where(d => d.IsSelected).Select(d => d.Letter).ToList();
        _store.Save(_config);
        RaiseSelectionSummary();
    }

    private void OnDiskSelectionChanged(DiskItem item)
    {
        if (_suppressSave) return;

        var selected = Disks.Where(d => d.IsSelected).Select(d => d.Letter).ToList();

        // 保留当前不在列表中的历史盘符（例如暂时离线的固定盘），避免误删用户选择
        var missing = _config.SelectedDrives?
            .Where(letter => !Disks.Any(d => string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase)))
            ?? Enumerable.Empty<string>();

        _config.SelectedDrives = selected
            .Concat(missing)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _store.Save(_config);
        RaiseSelectionSummary();
    }

    private void RaiseSelectionSummary()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(SelectAllText));

        if (_isBusy) return;

        var summary = SelectedCount > 0
            ? $"已选 {SelectedCount} 块磁盘 · 机械硬盘会深度整理，固态硬盘只做优化 · 开始时会请求管理员权限"
            : "还没有选磁盘 · 勾选后就可以开始整理";

        // 数据目录降级（%LocalAppData% 不可写）必须让用户看到，不能只写在日志里
        _footerHint = AppPaths.LocationWarning.Length > 0
            ? AppPaths.LocationWarning
            : summary;
        OnPropertyChanged(nameof(FooterHint));
        if (AppPaths.LocationWarning.Length > 0 && !_locationWarningShown)
        {
            _locationWarningShown = true;
            NotificationRequested?.Invoke("AutoDefrag", AppPaths.LocationWarning, NotifyLevel.Warning);
        }
    }

    // ---------------------------------------------------------------- 设置项（即时保存）

    public bool ScheduleEnabled
    {
        get => _config.ScheduleEnabled;
        set
        {
            if (_config.ScheduleEnabled == value) return;
            _config.ScheduleEnabled = value;
            _store.Save(_config);
            Logger.Info(value ? "每日自动整理已启用" : "每日自动整理已暂停");
            OnPropertyChanged();
            OnPropertyChanged(nameof(NextRunText));
        }
    }

    public void ToggleSchedule() => ScheduleEnabled = !ScheduleEnabled;

    public bool RunAtStartup
    {
        get => _config.RunAtStartup;
        set
        {
            if (_config.RunAtStartup == value) return;

            if (value)
            {
                if (ClockRegex.IsMatch(_config.DailyTime)) _lastClock = _config.DailyTime;
                _config.DailyTime = "0";
            }
            else
            {
                _config.DailyTime = ClockRegex.IsMatch(_lastClock) ? _lastClock : "03:00";
            }

            _isTimeInvalid = false;
            _store.Save(_config);
            Logger.Info(value
                ? "自动整理改为：当天首次启动时执行一次"
                : $"自动整理改为：每天 {_config.DailyTime}");

            OnPropertyChanged();
            OnPropertyChanged(nameof(TimeText));
            OnPropertyChanged(nameof(TimeRowVisibility));
            OnPropertyChanged(nameof(TimeErrorVisibility));
            OnPropertyChanged(nameof(NextRunText));
        }
    }

    public string TimeText => _config.RunAtStartup ? _lastClock : _config.DailyTime;

    public Visibility TimeRowVisibility =>
        _config.RunAtStartup ? Visibility.Collapsed : Visibility.Visible;

    public bool IsTimeInvalid => _isTimeInvalid;

    public Visibility TimeErrorVisibility =>
        _isTimeInvalid ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>由时间输入框在失焦/回车时调用；非法格式返回 false 并显示提示。</summary>
    public bool TrySetTime(string text)
    {
        var value = (text ?? string.Empty).Trim();
        if (!ClockRegex.IsMatch(value))
        {
            _isTimeInvalid = true;
            OnPropertyChanged(nameof(IsTimeInvalid));
            OnPropertyChanged(nameof(TimeErrorVisibility));
            return false;
        }

        _isTimeInvalid = false;
        _config.DailyTime = value;
        _lastClock = value;
        _store.Save(_config);
        Logger.Info("每日执行时间已改为 " + value);

        OnPropertyChanged(nameof(IsTimeInvalid));
        OnPropertyChanged(nameof(TimeErrorVisibility));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(RunAtStartup));
        OnPropertyChanged(nameof(NextRunText));
        return true;
    }

    public bool AutoStart
    {
        get => _config.AutoStart;
        set
        {
            if (_config.AutoStart == value) return;
            _config.AutoStart = value;
            _store.Save(_config);
            AutoStartService.Sync(value);
            Logger.Info("开机自启已" + (value ? "开启" : "关闭"));
            OnPropertyChanged();
        }
    }

    public bool MinimizeToTray
    {
        get => _config.MinimizeToTray;
        set
        {
            if (_config.MinimizeToTray == value) return;
            _config.MinimizeToTray = value;
            _store.Save(_config);
            Logger.Info(value ? "关闭窗口时最小化到托盘" : "关闭窗口时退出程序");
            OnPropertyChanged();
        }
    }

    /// <summary>整理完成时播放提示音（默认开启；失败或被停止时不播放）。</summary>
    public bool CompletionSound
    {
        get => _config.CompletionSound;
        set
        {
            if (_config.CompletionSound == value) return;
            _config.CompletionSound = value;
            _store.Save(_config);
            Logger.Info("整理完成提示音已" + (value ? "开启" : "关闭"));
            OnPropertyChanged();
        }
    }

    public double CpuThreshold
    {
        get => _config.CpuThreshold;
        set
        {
            var clamped = Math.Clamp((int)Math.Round(value), 10, 100);
            if (_config.CpuThreshold == clamped) return;
            _config.CpuThreshold = clamped;
            _store.Save(_config);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CpuThresholdText));
        }
    }

    public string CpuThresholdText => $"{_config.CpuThreshold}%";

    public string ThemeMode => _config.Theme;

    public void SetTheme(string mode)
    {
        if (mode is not ("System" or "Light" or "Dark")) return;
        if (_config.Theme == mode) return;

        _config.Theme = mode;
        _store.Save(_config);
        ThemeService.Apply(mode);
        Logger.Info("主题已切换: " + mode);
        OnPropertyChanged(nameof(ThemeMode));
    }

    public string NextRunText
    {
        get
        {
            if (!_config.ScheduleEnabled) return "自动整理已暂停";
            if (_config.RunAtStartup) return "当天首次启动时";
            if (LastRunIsToday()) return $"明天 {_config.DailyTime}";

            return DateTime.Now < TargetTimeToday()
                ? $"今天 {_config.DailyTime}"
                : "即将执行";
        }
    }

    public string LastResultText
    {
        get => _lastResultText;
        private set
        {
            if (_lastResultText == value) return;
            _lastResultText = value;
            OnPropertyChanged();
        }
    }

    private void RefreshLastResult()
    {
        try
        {
            var raw = File.Exists(AppPaths.LastResultFile)
                ? File.ReadAllText(AppPaths.LastResultFile).Trim()
                : string.Empty;
            LastResultText = string.IsNullOrWhiteSpace(raw) ? "暂无执行记录" : raw;
        }
        catch
        {
            LastResultText = "暂无执行记录";
        }
    }

    // ---------------------------------------------------------------- 状态展示

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string FooterHint
    {
        get => _footerHint;
        private set
        {
            if (_footerHint == value) return;
            _footerHint = value;
            OnPropertyChanged();
        }
    }

    public string StartButtonText => _previewing ? "正在预览…" : _isBusy ? "正在整理…" : "立即整理";

    /// <summary>忙时显示“停止整理”，让用户能体面中止（当前文件做完就停）。</summary>
    public Visibility StopButtonVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;

    public string EngineMode
    {
        get => _config.EngineMode;
        set
        {
            if (!EngineResolver.IsValid(value)) return;
            if (_config.EngineMode == value) return;

            _config.EngineMode = value;
            _store.Save(_config);
            Logger.Info("整理方式已切换: " + EngineModeText);
            OnPropertyChanged();
            OnPropertyChanged(nameof(EngineModeText));
            OnPropertyChanged(nameof(EngineModeHint));
            RefreshDisks();
        }
    }

    public string EngineModeText => EngineResolver.ModeText(_config.EngineMode);

    public string EngineModeHint => _config.EngineMode switch
    {
        EngineResolver.System =>
            "调用 Windows 自带的 defrag.exe，和 1.2.0 相同；对每块大于 4KB 的磁盘无效，会提示跳过而不是假装成功。",
        EngineResolver.Auto =>
            "推荐。按每块盘的存储块大小自动选：大于 4KB 用本程序整理，不超过 4KB 用 Windows 自带整理。已有用户保存过的选择不会被改动。",
        _ =>
            "大容量存储块磁盘（每块大于 4KB）由本程序重写文件来整理；小存储块磁盘仍调用系统 defrag，效果更稳。重写会完整读写一遍文件，耗时约等于复制。"
    };

    /// <summary>只整理碎片较多的文件（关闭时全部整理）。</summary>
    public bool FragThresholdEnabled
    {
        get => _config.FragThresholdEnabled;
        set
        {
            if (_config.FragThresholdEnabled == value) return;
            _config.FragThresholdEnabled = value;
            _store.Save(_config);
            Logger.Info("整理范围已切换: " + FragThresholdText);
            OnPropertyChanged();
            OnPropertyChanged(nameof(FragThresholdText));
        }
    }

    /// <summary>文件级碎片率阈值（1–15）；越界值直接忽略，由设置界面负责提示。</summary>
    public int FragThresholdPercent
    {
        get => _config.FragThresholdPercent;
        set
        {
            if (value < 1 || value > 15) return;
            if (_config.FragThresholdPercent == value) return;
            _config.FragThresholdPercent = value;
            _store.Save(_config);
            Logger.Info("文件碎片率阈值已改为 " + value + "%");
            OnPropertyChanged();
            OnPropertyChanged(nameof(FragThresholdText));
        }
    }

    public string FragThresholdText =>
        _config.FragThresholdEnabled
            ? $"只整理碎片率 ≥ {_config.FragThresholdPercent}% 的文件"
            : "全部整理";

    /// <summary>设置窗口读取的整理偏好快照（含三档预设与高级参数）。</summary>
    public DefragPreferences DefragPreferences => _config.GetDefragPreferences();

    /// <summary>设置窗口点“保存”时一次性写回；写盘后立即通知相关只读属性刷新。</summary>
    public void ApplyDefragSettings(DefragPreferences prefs)
    {
        _config.ApplyDefragPreferences(prefs);
        _store.Save(_config);
        RefreshDisks();
        Logger.Info($"整理设置已保存: 质量={_config.DefragQuality}, 最小文件={_config.MinFileSizeKB}KB, " +
                    $"单轮≤{_config.MaxFilesPerRun}文件/{_config.MaxBytesPerRunGB}GB, 可回收≥{_config.MinReclaimMB}MB, " +
                    $"校验={_config.VerifyMode}, 最多{_config.MaxRounds}轮/{_config.MaxRunMinutes}分钟, " +
                    $"碎片率阈值={(FragThresholdEnabled ? $"≥{FragThresholdPercent}%" : "关闭")}, " +
                    $"固态只优化={(_config.SsdOptimizeOnly ? "是" : "否")}, 机械盘空闲合并={(_config.MergeFreeSpace ? "是" : "否")}");
        OnPropertyChanged(nameof(FragThresholdEnabled));
        OnPropertyChanged(nameof(FragThresholdPercent));
        OnPropertyChanged(nameof(FragThresholdText));
        OnPropertyChanged(nameof(LargeClusterHint));
        OnPropertyChanged(nameof(LargeClusterHintVisibility));
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(StartButtonText));
            OnPropertyChanged(nameof(StopButtonVisibility));
            RaiseProgressStateChanged();
        }
    }

    // ---------------------------------------------------------------- 底部进度区

    /// <summary>收起/展开状态持久化在 config.json 的 ProgressExpanded。</summary>
    public bool ProgressExpanded
    {
        get => _config.ProgressExpanded;
        private set
        {
            if (_config.ProgressExpanded == value) return;
            _config.ProgressExpanded = value;
            _store.Save(_config);
            Logger.Info(value ? "进度详情已展开" : "进度详情已收起");
            OnPropertyChanged();
            RaiseProgressStateChanged();
        }
    }

    public void ToggleProgressExpanded() => ProgressExpanded = !ProgressExpanded;

    /// <summary>整理或预览进行中；空闲时不显示进度条。</summary>
    public bool ProgressActive => _isBusy || _previewing;

    public Visibility ProgressCollapsedVisibility =>
        !ProgressExpanded ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProgressExpandedVisibility =>
        ProgressExpanded ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ProgressTrackVisibility =>
        ProgressActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>百分比未知时走不确定态（动画条），不编造进度。</summary>
    public bool ProgressIndeterminate => ProgressActive && _progressPercent < 0;

    public double ProgressPercentValue => _progressPercent < 0 ? 0 : Math.Clamp(_progressPercent, 0, 100);

    public string ProgressPercentText => _progressPercentText;
    public string ProgressTitle => _progressTitle;
    public string ProgressDetail => _progressDetail;
    public string ProgressGlyph => ProgressExpanded ? "▾" : "▸";
    public string ProgressToggleTip => ProgressExpanded ? "收起整理详情" : "展开整理详情";

    /// <summary>类 cmd 的滚动日志，最多保留最近 200 行。</summary>
    public string ProgressLogText
    {
        get => _progressLogText;
        private set
        {
            if (_progressLogText == value) return;
            _progressLogText = value;
            OnPropertyChanged();
        }
    }

    private void RaiseProgressStateChanged()
    {
        OnPropertyChanged(nameof(ProgressActive));
        OnPropertyChanged(nameof(ProgressCollapsedVisibility));
        OnPropertyChanged(nameof(ProgressExpandedVisibility));
        OnPropertyChanged(nameof(ProgressTrackVisibility));
        OnPropertyChanged(nameof(ProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressPercentValue));
        OnPropertyChanged(nameof(ProgressPercentText));
        OnPropertyChanged(nameof(ProgressTitle));
        OnPropertyChanged(nameof(ProgressDetail));
        OnPropertyChanged(nameof(ProgressGlyph));
        OnPropertyChanged(nameof(ProgressToggleTip));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartButtonText));
    }

    private void SetProgress(string title, string detail, int percent)
    {
        _progressTitle = title;
        _progressDetail = detail;
        _progressPercent = percent >= 0 ? Math.Clamp(percent, 0, 100) : -1;
        _progressPercentText = _progressPercent >= 0
            ? $"{_progressPercent}%"
            : ProgressActive ? "…" : "就绪";
        RaiseProgressStateChanged();
    }

    /// <summary>新任务开始：清空上一次的日志与百分比，避免界面显示旧的“50%”。</summary>
    private void BeginProgress(string title)
    {
        _progressLines.Clear();
        _progressLogText = string.Empty;
        _progressOffset = 0;
        _stopping = false;
        _completionSoundPlayed = false;
        SetProgress(title, string.Empty, -1);
        OnPropertyChanged(nameof(ProgressLogText));
    }

    private void FinishProgress(string detail)
    {
        _stopping = false;
        _progressPercent = -1;
        _progressPercentText = "就绪";
        _progressTitle = "就绪";
        _progressDetail = detail;
        RaiseProgressStateChanged();
        RefreshProgressLog();
    }

    /// <summary>增量读取 progress.log，只保留最近 N 行；同时更新界面日志文本。</summary>
    private void RefreshProgressLog()
    {
        var (lines, offset) = ProgressLog.ReadNew(_progressOffset);
        _progressOffset = offset;
        if (lines.Count == 0) return;

        foreach (var line in lines) _progressLines.Add(line);
        while (_progressLines.Count > ProgressLogMaxLines)
        {
            _progressLines.RemoveAt(0);
        }

        ProgressLogText = string.Join(Environment.NewLine, _progressLines);
    }

    // ---------------------------------------------------------------- 触发任务

    public void StartCleanManual() => StartClean(auto: false);

    private void StartClean(bool auto)
    {
        string? gateBusy = null;
        if (_worker.IsRunning || _previewing || TaskGate.IsBusy(out gateBusy))
        {
            FooterHint = gateBusy ?? "已有任务正在运行，请稍候";
            if (!auto && gateBusy is not null)
                NotificationRequested?.Invoke("AutoDefrag", gateBusy, NotifyLevel.Warning);
            return;
        }

        // 自动整理也要尊重用户在磁盘列表里的勾选；首次启动未配置时才回退为全部固定磁盘
        var present = DiskService.GetFixedDrives().Select(DiskService.Letter).ToList();
        var selected = _config.SelectedDrives ?? present;
        var drives = auto
            ? present.Where(letter => selected.Any(s => string.Equals(s, letter, StringComparison.OrdinalIgnoreCase))).ToList()
            : Disks.Where(d => d.IsSelected).Select(d => d.Letter).ToList();

        if (drives.Count == 0)
        {
            StatusText = "未选择磁盘";
            FooterHint = "请先在磁盘列表中勾选至少一个磁盘";
            if (!auto)
                NotificationRequested?.Invoke("AutoDefrag", "请先在主界面勾选要整理的磁盘", NotifyLevel.Warning);
            return;
        }

        var prefs = _config.GetDefragPreferences();
        if (!_worker.Start("clean", drives.ToArray(), _config.CpuThreshold, auto, out var error, _config.EngineMode,
                _config.FragThresholdEnabled ? _config.FragThresholdPercent : 0, prefs))
        {
            StatusText = "空闲";
            FooterHint = error ?? "无法开始整理";
            if (!auto)
                NotificationRequested?.Invoke("AutoDefrag", error ?? "无法开始整理", NotifyLevel.Warning);
            return;
        }

        var qualityName = DefragQualityPresets.Match(prefs)?.Name
                          ?? (prefs.Quality == DefragQualityPresets.Custom ? "自定义" : DefragQualityPresets.Get(prefs.Quality).Name);
        IsBusy = true;
        StatusText = auto ? "自动整理中" : "正在整理";
        FooterHint = $"正在整理 {string.Join("、", drives)}（{EngineModeText}，质量={qualityName}）· " +
                     (_config.FragThresholdEnabled ? $"仅整理碎片率 ≥ {_config.FragThresholdPercent}% 的文件 · " : "全部整理 · ") +
                     $"预计最多 {prefs.MaxRounds} 轮 · CPU 连续 10 秒高于 {_config.CpuThreshold}% 时暂停，回落后自动继续";
        BeginProgress($"正在准备整理 {string.Join("、", drives)}");
    }

    /// <summary>请求停止：写停止标记，大簇卷在当前文件结束后停，小簇卷直接结束 defrag 进程。</summary>
    public void StopClean()
    {
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(AppPaths.StopFlagFile,
                $"stop requested at {DateTime.Now:yyyy-MM-dd HH:mm:ss}", System.Text.UTF8Encoding.UTF8);
            StatusText = "正在停止";
            FooterHint = "已请求停止：当前文件做完就停，已完成的文件不会回滚";
            _stopping = true;
            SetProgress("正在停止", "等待当前文件处理完，已完成的文件不会回滚", -1);
            NotificationRequested?.Invoke("AutoDefrag", "已请求停止，当前文件做完就会停下", NotifyLevel.Info);
            Logger.Info("用户请求停止整理");
        }
        catch (Exception ex)
        {
            FooterHint = "停止请求失败: " + ex.Message;
            Logger.Warn("写入停止标记失败: " + ex.Message);
        }
    }

    public void StartProbe()
    {
        string? gateBusy = null;
        if (_worker.IsRunning || _previewing || TaskGate.IsBusy(out gateBusy))
        {
            FooterHint = gateBusy ?? "已有任务正在运行，请稍候";
            if (gateBusy is not null)
                NotificationRequested?.Invoke("AutoDefrag", gateBusy, NotifyLevel.Warning);
            return;
        }

        var drives = DiskService.GetFixedDrives().Select(DiskService.Letter).ToArray();
        if (drives.Length == 0)
        {
            FooterHint = "未检测到本地固定磁盘";
            return;
        }

        if (!_worker.Start("status", drives, _config.CpuThreshold, false, out var error))
        {
            StatusText = "空闲";
            FooterHint = error ?? "无法开始检查";
            NotificationRequested?.Invoke("AutoDefrag", error ?? "检查失败", NotifyLevel.Warning);
            return;
        }

        IsBusy = true;
        StatusText = "正在检查磁盘";
        FooterHint = "正在读取各磁盘的碎片情况（需要管理员权限）";
        BeginProgress("正在检查磁盘碎片");
    }

    /// <summary>托盘“预览整理”：分析大簇卷并写报告，不动任何文件；进度区同样显示过程。</summary>
    public void StartPreview()
    {
        string? gateBusy = null;
        if (_worker.IsRunning || _isBusy || _previewing || TaskGate.IsBusy(out gateBusy))
        {
            FooterHint = gateBusy ?? "已有任务正在运行，请稍候";
            if (gateBusy is not null)
                NotificationRequested?.Invoke("AutoDefrag", gateBusy, NotifyLevel.Warning);
            return;
        }

        var drives = DiskService.GetFixedDrives().Select(DiskService.Letter)
            .Where(letter => DiskService.IsLargeCluster(Native.GetClusterSize(letter)))
            .ToList();

        if (drives.Count == 0)
        {
            NotificationRequested?.Invoke("AutoDefrag",
                "没有大于 4KB 存储块的固定磁盘，预览整理只对这些磁盘有意义",
                NotifyLevel.Info);
            return;
        }

        _previewing = true;
        StatusText = "正在预览";
        FooterHint = $"正在预览 {string.Join("、", drives)} 的整理计划（只读分析，不会改动文件）";
        BeginProgress($"正在预览整理计划 {string.Join("、", drives)}");
        RaiseProgressStateChanged();

        var previewDrives = drives.ToArray();
        System.Threading.Tasks.Task.Run(() =>
        {
            var code = 3;
            try
            {
                code = Rewrite.RewriteCommand.RunDrives(previewDrives, dryRun: true, quiet: false);
            }
            catch (Exception ex)
            {
                Logger.Warn("预览整理失败: " + ex.Message);
                code = 1;
            }
            finally
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _previewing = false;
                    if (code == 3)
                    {
                        StatusText = "空闲";
                        FooterHint = "已有另一个整理/预览任务正在运行，本次预览没有执行";
                        FinishProgress("已有另一个任务正在运行");
                        NotificationRequested?.Invoke("AutoDefrag",
                            "已有另一个整理/预览任务正在运行，本次预览没有执行", NotifyLevel.Warning);
                        return;
                    }

                    StatusText = "空闲";
                    FooterHint = "预览完成：只读分析，没有改动任何文件";
                    FinishProgress("预览完成（未改动任何文件）");
                    NotificationRequested?.Invoke("AutoDefrag",
                        "预览完成，报告已写入 " + AppPaths.RewritePreviewFile, NotifyLevel.Info);
                }));
            }
        });
    }

    // ---------------------------------------------------------------- worker 看门狗

    private void OnWatchTick()
    {
        if (_worker.IsRunning)
        {
            UpdateProgress();
            return;
        }

        if (_worker.TryComplete(out var mode, out var auto, out var exitCode))
        {
            HandleCompletion(mode, auto, exitCode);
            return;
        }

        // 预览在应用内后台线程运行，也需要刷新进度区
        if (_previewing)
        {
            UpdateProgress();
            return;
        }

        if (_isBusy)
        {
            IsBusy = false;
            StatusText = "空闲";
            FinishProgress("上次任务已结束");
        }
    }

    private void UpdateProgress()
    {
        RefreshProgressLog();

        var state = WorkerStateFile.TryLoad();
        if (state is null) return;

        if (state.Stage == "完成")
        {
            StatusText = "空闲";
            FooterHint = state.Message;
            SetProgress(_stopping ? "正在停止" : "收尾中", state.Message, _stopping ? -1 : state.Percent);
            return;
        }

        var title = BuildProgressTitle(state);
        var detail = BuildProgressDetail(state);

        if (string.Equals(state.Mode, "status", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "正在检查磁盘";
            FooterHint = $"{title} · {detail}";
            SetProgress(title, detail, state.Percent);
            return;
        }

        if (_stopping)
        {
            StatusText = "正在停止";
            FooterHint = "已请求停止，等待当前文件完成";
            SetProgress("正在停止", detail, -1);
            return;
        }

        if (state.Stage == "分析碎片")
        {
            StatusText = _worker.IsAuto ? "自动整理中" : "正在整理";
            FooterHint = $"{title} · {detail}";
            SetProgress(title, detail, state.Percent);
            return;
        }

        var verb = state.Stage == "正在优化" ? "优化" : "整理";
        StatusText = _worker.IsAuto ? $"自动{verb}中" : $"正在{verb}";
        var cpuInfo = state.CpuPercent > 0 ? $" · CPU {state.CpuPercent:0.#}%" : string.Empty;
        FooterHint = $"{title} · {detail}{cpuInfo}";
        SetProgress(title, detail, state.Percent);
    }

    private static string BuildProgressTitle(WorkerStateFile state)
    {
        var drive = state.DriveLabel.Length > 0 ? state.DriveLabel + ":" : state.Drive;
        var mode = state.Mode;
        if (string.Equals(mode, "status", StringComparison.OrdinalIgnoreCase))
            return $"正在检查 {drive}（第 {state.DriveIndex}/{state.DriveTotal} 块磁盘）";
        if (string.Equals(mode, "rewrite-preview", StringComparison.OrdinalIgnoreCase))
            return $"正在预览 {drive} 的整理计划";
        return $"正在整理 {drive}（第 {state.DriveIndex}/{state.DriveTotal} 块磁盘）";
    }

    private static string BuildProgressDetail(WorkerStateFile state)
    {
        var parts = new List<string>();
        if (state.EngineDetail.Length > 0) parts.Add(state.EngineDetail);

        var stage = state.Stage;
        if (state.Phase.Length > 0 && !stage.EndsWith(state.Phase, StringComparison.Ordinal))
            stage += " · " + state.Phase;
        if (stage.Length > 0) parts.Add(stage);

        if (state.RoundTotal > 0 && state.Round > 0)
            parts.Add($"第 {state.Round}/{state.RoundTotal} 轮");

        if (state.CoveragePercent > 0)
            parts.Add($"覆盖率 {state.CoveragePercent:0.#}%（仅已分析文件）");

        if (state.FileTotal > 0) parts.Add($"文件 {state.FileIndex}/{state.FileTotal}");
        if (state.CurrentFile.Length > 0) parts.Add(state.CurrentFile);

        if (state.TotalBytes > 0)
            parts.Add($"{DiskService.FormatBytes(state.ProcessedBytes)}/{DiskService.FormatBytes(state.TotalBytes)}");
        if (state.SpeedBytesPerSec > 0) parts.Add($"{DiskService.FormatBytes(state.SpeedBytesPerSec)}/s");

        return string.Join(" · ", parts);
    }

    private void HandleCompletion(string mode, bool auto, int? exitCode)
    {
        IsBusy = false;
        var ok = exitCode == 0;

        Logger.Info($"后台任务已结束 (模式={(string.Equals(mode, "clean", StringComparison.OrdinalIgnoreCase) ? "整理" : "检查")}, Auto={auto}, Exit={exitCode?.ToString() ?? "未知"})");

        // exit=3：任务级互斥未拿到（已有另一个整理/预览任务在跑），本次没有执行
        if (exitCode == 3)
        {
            StatusText = "空闲";
            FooterHint = "已有另一个整理/预览任务正在运行，本次没有启动";
            FinishProgress("已有另一个任务正在运行");
            if (!auto)
            {
                NotificationRequested?.Invoke("AutoDefrag",
                    "已有另一个整理/预览任务正在运行，本次没有启动", NotifyLevel.Warning);
            }
            OnPropertyChanged(nameof(NextRunText));
            return;
        }

        // exit=4：用户主动停止，已完成的整理效果保留，不能按“成功/部分失败”文案展示
        if (exitCode == 4)
        {
            if (string.Equals(mode, "clean", StringComparison.OrdinalIgnoreCase))
            {
                RefreshLastResult();
                StatusText = "已停止";
                FooterHint = "已按请求停止：已完成的整理效果保留，未完成的部分下次整理会继续";
                FinishProgress("整理已停止");
                NotificationRequested?.Invoke("AutoDefrag",
                    auto ? "自动整理已按请求停止" : "磁盘整理已停止（已完成的部分保留）", NotifyLevel.Info);
            }
            else
            {
                RefreshDisks();
                StatusText = "空闲";
                FooterHint = "碎片检查已停止";
                FinishProgress("碎片检查已停止");
            }
            OnPropertyChanged(nameof(NextRunText));
            return;
        }

        if (string.Equals(mode, "clean", StringComparison.OrdinalIgnoreCase))
        {
            RefreshLastResult();
            StatusText = ok ? "已完成" : "完成，部分未成功";
            FooterHint = ok ? "整理完成，结果已记录" : "整理结束，有磁盘没能完成，点“查看日志”可以看原因";
            FinishProgress(ok ? "上次整理已完成" : "上次整理有磁盘未完成");

            var message = ok
                ? (auto ? "今天的自动整理已完成" : "磁盘整理已完成")
                : (auto ? "今天的自动整理结束，有磁盘没能完成" : "整理结束，有磁盘没能完成");
            NotificationRequested?.Invoke("AutoDefrag", message, ok ? NotifyLevel.Info : NotifyLevel.Warning);

            // 只有真正成功完成才响；失败、被停止（exit=4）在上面已经返回，不会走到这里
            if (ok) PlayCompletionSoundOnce();
        }
        else
        {
            RefreshDisks();
            StatusText = "空闲";
            FooterHint = ok ? "磁盘信息已更新" : "部分磁盘未能读取碎片信息，详情见日志";
            FinishProgress(ok ? "磁盘碎片检查完成" : "碎片检查有磁盘读取失败");
        }

        OnPropertyChanged(nameof(NextRunText));
    }

    /// <summary>一次任务最多响一次，避免 watcher 与完成回调同时到达时重复播放。</summary>
    private void PlayCompletionSoundOnce()
    {
        if (_completionSoundPlayed) return;
        _completionSoundPlayed = true;

        if (!_config.CompletionSound)
        {
            Logger.Info("完成提示音已跳过：设置已关闭");
            return;
        }

        // 属性与本类同名的静态类，需要写全名避免解析成 bool 属性
        AutoDefrag.CompletionSound.Play();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
