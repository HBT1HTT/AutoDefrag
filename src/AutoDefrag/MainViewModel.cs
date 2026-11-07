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
        Logger.Info($"开机调度判定: {decision} (DailyTime={_config.DailyTime}, ScheduleEnabled={_config.ScheduleEnabled})");

        if (decision == "done")
        {
            NotificationRequested?.Invoke("AutoDefrag", "今日磁盘维护已完成，无需重复执行", NotifyLevel.Info);
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
        Logger.Info("每日调度触发清理: DailyTime=" + _config.DailyTime);
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
        var status = DriveStatusFile.TryLoad();
        var frags = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        if (status is not null)
        {
            foreach (var entry in status.Drives)
                frags[entry.Drive] = entry.FragPercent;
        }

        var firstRun = _config.SelectedDrives is null;

        _suppressSave = true;
        Disks.Clear();
        foreach (var drive in DiskService.GetFixedDrives())
        {
            var letter = DiskService.Letter(drive);
            var selected = _config.SelectedDrives is null ||
                           _config.SelectedDrives.Any(x => string.Equals(x, letter, StringComparison.OrdinalIgnoreCase));
            var fragText = frags.TryGetValue(letter, out var frag) && frag is not null
                ? $"{frag:0.##}%"
                : "--";

            Disks.Add(new DiskItem(
                letter,
                DiskService.VolumeLabel(drive),
                DiskService.TypeText(drive),
                DiskService.SizeText(drive),
                fragText,
                selected,
                OnDiskSelectionChanged));
        }

        if (firstRun)
        {
            _config.SelectedDrives = Disks.Where(d => d.IsSelected).Select(d => d.Letter).ToList();
            _store.Save(_config);
            Logger.Info("首次运行：默认勾选全部本地固定磁盘 (" + string.Join(",", _config.SelectedDrives) + ")");
        }
        _suppressSave = false;

        OnPropertyChanged(nameof(HasDisks));
        OnPropertyChanged(nameof(EmptyHintVisibility));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(SelectAllText));
        RaiseSelectionSummary();
    }

    public bool HasDisks => Disks.Count > 0;
    public Visibility EmptyHintVisibility => Disks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
            ? $"已勾选 {SelectedCount} 个磁盘 · 优化时会请求管理员权限"
            : "尚未勾选磁盘 · 勾选后即可立即优化";
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
            Logger.Info(value ? "每日调度已启用" : "每日调度已暂停");
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
                ? "调度改为：程序启动后立即执行一次"
                : $"调度改为：每日 {_config.DailyTime}");

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
            if (!_config.ScheduleEnabled) return "调度已暂停";
            if (_config.RunAtStartup) return "下次程序启动时";
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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanStart));
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
                NotificationRequested?.Invoke("AutoDefrag", "请先在主界面勾选要优化的磁盘", NotifyLevel.Warning);
            return;
        }

        if (!_worker.Start("clean", drives.ToArray(), _config.CpuThreshold, auto, out var error))
        {
            StatusText = "空闲";
            FooterHint = error ?? "启动失败";
            if (!auto)
                NotificationRequested?.Invoke("AutoDefrag", error ?? "启动失败", NotifyLevel.Warning);
            return;
        }

        IsBusy = true;
        StatusText = auto ? "自动优化中" : "正在优化";
        FooterHint = $"目标 {string.Join("、", drives)} · CPU 高于 {_config.CpuThreshold}% 时自动暂停";
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
            FooterHint = error ?? "启动失败";
            NotificationRequested?.Invoke("AutoDefrag", error ?? "探测失败", NotifyLevel.Warning);
            return;
        }

        IsBusy = true;
        StatusText = "正在探测";
        FooterHint = "正在获取各磁盘碎片率（需要管理员权限）";
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
            StatusText = "正在探测";
            FooterHint = $"正在探测 {state.Drive} · {state.DriveIndex}/{state.DriveTotal}";
            return;
        }

        if (state.Stage == "分析碎片")
        {
            StatusText = _worker.IsAuto ? "自动优化中" : "正在优化";
            FooterHint = $"{state.Drive} · 正在分析碎片率";
            return;
        }

        StatusText = _worker.IsAuto ? "自动优化中" : "正在优化";
        var progress = state.DriveTotal > 1 ? $"{state.DriveIndex}/{state.DriveTotal} " : string.Empty;
        FooterHint = $"{progress}{state.Drive} · CPU {state.CpuPercent:0.#}% · 已暂停 {state.Suspended} 次";
    }

    private void HandleCompletion(string mode, bool auto, int? exitCode)
    {
        IsBusy = false;
        var ok = exitCode == 0;

        Logger.Info($"后台引擎已退出 (Mode={mode}, Auto={auto}, Exit={exitCode?.ToString() ?? "未知"})");

        if (string.Equals(mode, "clean", StringComparison.OrdinalIgnoreCase))
        {
            RefreshLastResult();
            StatusText = ok ? "已完成" : "已完成·有失败";
            FooterHint = ok ? "整理完成，结果已记录" : "整理结束，部分磁盘未完成，详见日志";

            var message = ok
                ? (auto ? "每日磁盘维护已完成" : "磁盘优化已完成")
                : (auto ? "每日磁盘维护结束：有磁盘未完成" : "磁盘优化结束：有磁盘未完成");
            NotificationRequested?.Invoke("AutoDefrag", message, ok ? NotifyLevel.Info : NotifyLevel.Warning);
        }
        else
        {
            RefreshDisks();
            StatusText = "空闲";
            FooterHint = "碎片率已更新";
        }

        OnPropertyChanged(nameof(NextRunText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
