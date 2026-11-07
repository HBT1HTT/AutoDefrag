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

        if (_worker.IsRunning || _isBusy) return;
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
        var largeClusterNotes = new List<string>();

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
                toolTip = $"这块磁盘的存储块比较大（每块 {DiskService.ClusterText(clusterBytes)}），Windows 自带的整理工具可能整理不了，结果会显示成“可能不支持”，具体可以看日志。";
                largeClusterNotes.Add($"{letter} 的存储块比较大（每块 {DiskService.ClusterText(clusterBytes)}），Windows 自带整理工具可能整理不了");
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

        _largeClusterHint = largeClusterNotes.Count > 0
            ? string.Join("；", largeClusterNotes) + "。整理结果会如实显示成“可能不支持”，具体可以看日志。"
            : string.Empty;

        OnPropertyChanged(nameof(HasDisks));
        OnPropertyChanged(nameof(LargeClusterHint));
        OnPropertyChanged(nameof(LargeClusterHintVisibility));
        OnPropertyChanged(nameof(EmptyHintVisibility));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(SelectAllText));
        RaiseSelectionSummary();
    }

    public bool HasDisks => Disks.Count > 0;
    public Visibility EmptyHintVisibility => Disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>超大簇（&gt;4KB）卷的简短提示，正常情况下隐藏。</summary>
    public string LargeClusterHint => _largeClusterHint;

    public Visibility LargeClusterHintVisibility =>
        _largeClusterHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public int SelectedCount => Disks.Count(d => d.IsSelected);

    public bool CanStart => !_isBusy && SelectedCount > 0;

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

        _footerHint = SelectedCount > 0
            ? $"已选 {SelectedCount} 块磁盘 · 机械硬盘会深度整理，固态硬盘只做优化 · 开始时会请求管理员权限"
            : "还没有选磁盘 · 勾选后就可以开始整理";
        OnPropertyChanged(nameof(FooterHint));
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

    public string StartButtonText => _isBusy ? "正在整理…" : "立即整理";

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
        }
    }

    // ---------------------------------------------------------------- 触发任务

    public void StartCleanManual() => StartClean(auto: false);

    private void StartClean(bool auto)
    {
        if (_worker.IsRunning)
        {
            FooterHint = "已有任务正在运行，请稍候";
            return;
        }

        var drives = auto
            ? DiskService.GetFixedDrives().Select(DiskService.Letter).ToList()
            : Disks.Where(d => d.IsSelected).Select(d => d.Letter).ToList();

        if (drives.Count == 0)
        {
            StatusText = "未选择磁盘";
            FooterHint = "请先在磁盘列表中勾选至少一个磁盘";
            if (!auto)
                NotificationRequested?.Invoke("AutoDefrag", "请先在主界面勾选要整理的磁盘", NotifyLevel.Warning);
            return;
        }

        if (!_worker.Start("clean", drives.ToArray(), _config.CpuThreshold, auto, out var error))
        {
            StatusText = "空闲";
            FooterHint = error ?? "无法开始整理";
            if (!auto)
                NotificationRequested?.Invoke("AutoDefrag", error ?? "无法开始整理", NotifyLevel.Warning);
            return;
        }

        IsBusy = true;
        StatusText = auto ? "自动整理中" : "正在整理";
        FooterHint = $"正在整理 {string.Join("、", drives)} · CPU 占用超过 {_config.CpuThreshold}% 时会自动暂停";
    }

    public void StartProbe()
    {
        if (_worker.IsRunning)
        {
            FooterHint = "已有任务正在运行，请稍候";
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

        if (_isBusy)
        {
            IsBusy = false;
            StatusText = "空闲";
        }
    }

    private void UpdateProgress()
    {
        var state = WorkerStateFile.TryLoad();
        if (state is null) return;

        if (state.Stage == "完成")
        {
            StatusText = "空闲";
            FooterHint = state.Message;
            return;
        }

        if (string.Equals(state.Mode, "status", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "正在检查磁盘";
            FooterHint = $"正在检查 {state.Drive} · {state.DriveIndex}/{state.DriveTotal}";
            return;
        }

        if (state.Stage == "分析碎片")
        {
            StatusText = _worker.IsAuto ? "自动整理中" : "正在整理";
            FooterHint = $"{state.Drive} · 正在读取碎片情况";
            return;
        }

        var verb = state.Stage == "正在优化" ? "优化" : "整理";
        StatusText = _worker.IsAuto ? $"自动{verb}中" : $"正在{verb}";
        var progress = state.DriveTotal > 1 ? $"{state.DriveIndex}/{state.DriveTotal} " : string.Empty;
        FooterHint = $"{progress}{state.Drive} · CPU {state.CpuPercent:0.#}% · 已暂停 {state.Suspended} 次";
    }

    private void HandleCompletion(string mode, bool auto, int? exitCode)
    {
        IsBusy = false;
        var ok = exitCode == 0;

        Logger.Info($"后台任务已结束 (模式={(string.Equals(mode, "clean", StringComparison.OrdinalIgnoreCase) ? "整理" : "检查")}, Auto={auto}, Exit={exitCode?.ToString() ?? "未知"})");

        if (string.Equals(mode, "clean", StringComparison.OrdinalIgnoreCase))
        {
            RefreshLastResult();
            StatusText = ok ? "已完成" : "完成，部分未成功";
            FooterHint = ok ? "整理完成，结果已记录" : "整理结束，有磁盘没能完成，点“查看日志”可以看原因";

            var message = ok
                ? (auto ? "今天的自动整理已完成" : "磁盘整理已完成")
                : (auto ? "今天的自动整理结束，有磁盘没能完成" : "整理结束，有磁盘没能完成");
            NotificationRequested?.Invoke("AutoDefrag", message, ok ? NotifyLevel.Info : NotifyLevel.Warning);
        }
        else
        {
            RefreshDisks();
            StatusText = "空闲";
            FooterHint = "磁盘信息已更新";
        }

        OnPropertyChanged(nameof(NextRunText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
