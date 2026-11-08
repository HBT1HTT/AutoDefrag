using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace AutoDefrag;

public partial class App : System.Windows.Application
{
    public static App Instance => (App)Current;

    private readonly ConfigStore _store = new();
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayIcon;
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private MainViewModel? _vm;
    private bool _exiting;
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("UI 未处理异常: ", args.Exception);
        };

        // 提权 worker：同一个 exe 的另一身份，不创建托盘、不参与单实例判断
        if (WorkerHost.IsWorkerRequest(e.Args))
        {
            StartWorker(e.Args);
            return;
        }

        // 一键 dry-run：只读预览，不建托盘、不开主窗口，做完直接退出
        if (Rewrite.RewriteCommand.IsDryRunRequest(e.Args))
        {
            var code = Rewrite.RewriteCommand.Run(e.Args);
            Shutdown(code);
            return;
        }

        AppPaths.Ensure();
        Logger.Init("app");

        var silent = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        WriteStartupBanner(silent);

        _mutex = new Mutex(true, @"Local\AutoDefrag.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Logger.Info(silent
                ? "检测到已有实例；本次为静默启动，不唤起窗口，也不创建托盘"
                : "检测到已有实例，本次启动不再创建托盘，改为唤起已有窗口");
            SignalExistingInstance(showWindow: !silent);
            Shutdown(0);
            return;
        }

        StartShowWindowListener();

        var config = _store.Load(out var note);
        if (note is not null)
        {
            if (note.Contains("失败")) Logger.Warn(note);
            else Logger.Info(note);
        }

        Logger.Info(
            $"配置已载入: {AppPaths.ConfigFile} " +
            $"(DailyTime={config.DailyTime}, AutoStart={config.AutoStart}, " +
            $"CpuThreshold={config.CpuThreshold}, ScheduleEnabled={config.ScheduleEnabled}, " +
            $"SelectedDrives={(config.SelectedDrives is null ? "(首次运行: 默认全部)" : string.Join(",", config.SelectedDrives))}, " +
            $"UiFont={(string.IsNullOrWhiteSpace(config.UiFont) ? "(推荐默认)" : config.UiFont)})");

        AutoStartService.Sync(config.AutoStart);
        ThemeService.Apply(config.Theme);
        FontService.Apply(config.UiFont);

        _vm = new MainViewModel(config, _store);
        _vm.NotificationRequested += ShowNotification;

        CreateTray();

        _window = new MainWindow(_vm);
        if (!silent)
            _window.Show();

        _vm.Start();

        Logger.Info($"托盘已就绪 (PID={Environment.ProcessId}, 数据目录={AppPaths.Root})");
    }

    // ------------------------------------------------------------------ worker

    private void StartWorker(string[] args)
    {
        var thread = new Thread(() =>
        {
            var exitCode = 1;
            try
            {
                exitCode = WorkerHost.Execute(args);
            }
            catch (Exception ex)
            {
                Logger.Error("worker 顶层异常: ", ex);
            }
            finally
            {
                Dispatcher.Invoke(() => Shutdown(exitCode));
            }
        })
        {
            IsBackground = false,
            Name = "defrag-worker"
        };
        thread.Start();
    }

    // ------------------------------------------------------------------ 启动日志

    private static void WriteStartupBanner(bool silent)
    {
        var bootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var source = silent ? "开机自启(Run 键)" : "手动启动";
        Logger.Info(
            $"================ 启动会话 | 本次开机于 {bootTime:yyyy-MM-dd HH:mm:ss} " +
            $"| 会话ID={Process.GetCurrentProcess().SessionId} | PID={Environment.ProcessId} " +
            $"| 来源={source} ================");
    }

    // ------------------------------------------------------------------ 单实例

    /// <summary>
    /// 通知已有实例把主窗口叫出来。唯一实例刚启动时监听线程可能还没就绪，
    /// 因此重试几次，并把结果写日志——避免用户点了 exe 却“什么都没发生”。
    /// </summary>
    private void SignalExistingInstance(bool showWindow)
    {
        if (!showWindow) return;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(@"Local\AutoDefrag.ShowWindow");
                handle.Set();
                Logger.Info("已请求已有实例显示主窗口，本次启动退出");
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // 已有实例可能仍在初始化（监听线程未创建），稍等再试
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                Logger.Warn($"通知已有实例失败: {ex.Message}");
                return;
            }
        }

        Logger.Warn("已有实例未响应显示请求（可能正在启动或退出），本次启动退出");
    }

    private void StartShowWindowListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AutoDefrag.ShowWindow");
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_showEvent.WaitOne()) continue;
                    Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "show-window-listener"
        };
        thread.Start();
    }

    // ------------------------------------------------------------------ 托盘

    private void CreateTray()
    {
        _trayIcon = LoadTrayIcon();

        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "AutoDefrag · 磁盘整理"
        };

        var openItem = new WinForms.ToolStripMenuItem("打开主界面");
        openItem.Click += (_, _) => Ui(ShowMainWindow);

        var settingsItem = new WinForms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => Ui(ShowSettings);

        var runItem = new WinForms.ToolStripMenuItem("立即整理");
        runItem.Click += (_, _) => Ui(() => _vm?.StartCleanManual());

        var scheduleItem = new WinForms.ToolStripMenuItem("暂停自动整理");
        scheduleItem.Click += (_, _) => Ui(() => _vm?.ToggleSchedule());

        var stopItem = new WinForms.ToolStripMenuItem("停止整理");
        stopItem.Click += (_, _) => Ui(() => _vm?.StopClean());

        var previewItem = new WinForms.ToolStripMenuItem("预览整理（不动磁盘）");
        previewItem.Click += (_, _) => Ui(OpenPreview);

        var logItem = new WinForms.ToolStripMenuItem("查看日志");
        logItem.Click += (_, _) => Ui(OpenLog);

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Ui(ExitApp);

        _trayMenu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
        _trayMenu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            openItem,
            settingsItem,
            new WinForms.ToolStripSeparator(),
            runItem,
            stopItem,
            previewItem,
            scheduleItem,
            logItem,
            new WinForms.ToolStripSeparator(),
            exitItem
        });

        _trayMenu.Opening += (_, _) =>
        {
            scheduleItem.Text = _vm?.ScheduleEnabled == false ? "恢复自动整理" : "暂停自动整理";
            runItem.Text = _vm?.IsBusy == true ? "正在整理…" : "立即整理";
            runItem.Enabled = _vm?.IsBusy != true;
            stopItem.Enabled = _vm?.IsBusy == true;
        };

        _tray.ContextMenuStrip = _trayMenu;
        _tray.DoubleClick += (_, _) => Ui(ShowMainWindow);
    }

    private static Drawing.Icon? LoadTrayIcon()
    {
        try
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Assets/AutoDefrag.ico"));
            if (info?.Stream is null) return null;
            using var stream = info.Stream;
            return new Drawing.Icon(stream);
        }
        catch
        {
            return Drawing.SystemIcons.Application;
        }
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    // ------------------------------------------------------------------ 主窗口 / 通知 / 日志

    public void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ShowSettings()
    {
        if (_vm is null) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_vm);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ShowTrayHint()
    {
        if (_trayHintShown || _tray is null) return;
        _trayHintShown = true;
        _tray.ShowBalloonTip(3000, "AutoDefrag 仍在运行",
            "程序已最小化到托盘，右键托盘图标可打开主界面或立即整理。", WinForms.ToolTipIcon.Info);
    }

    private void ShowNotification(string title, string text, NotifyLevel level)
    {
        if (_tray is null) return;
        var icon = level switch
        {
            NotifyLevel.Warning => WinForms.ToolTipIcon.Warning,
            NotifyLevel.Error => WinForms.ToolTipIcon.Error,
            _ => WinForms.ToolTipIcon.Info
        };
        var timeout = Math.Clamp((_vm?.NotificationTimeout ?? 10) * 1000, 3000, 10000);
        _tray.ShowBalloonTip(timeout, title, text, icon);
    }

    /// <summary>托盘“预览整理”：只读分析大簇卷，生成报告，不改动任何文件；进度显示在主界面进度区。</summary>
    private void OpenPreview()
    {
        Logger.Info("托盘请求预览整理");
        _vm?.StartPreview();
    }

    public void OpenLog()
    {
        try
        {
            AppPaths.Ensure();
            var path = Logger.AppLogPath;
            if (!File.Exists(path))
                File.WriteAllText(path, "（暂无日志）" + Environment.NewLine, new System.Text.UTF8Encoding(false));

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Warn("打开日志失败: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 退出

    public void ExitApp()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ExitApp));
            return;
        }

        if (_exiting) return;
        _exiting = true;

        try
        {
            _settingsWindow?.Close();
            _window?.PrepareExit();
            _window?.Close();
        }
        catch
        {
            // 忽略关闭异常
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayIcon?.Dispose();
        _trayIcon = null;

        Logger.Info("程序退出");
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
            _trayMenu?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _showEvent?.Dispose();
            _mutex?.Dispose();
        }
        catch
        {
            // 忽略
        }

        base.OnExit(e);
    }
}
