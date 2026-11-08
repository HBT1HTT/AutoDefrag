using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ToolTip = System.Windows.Controls.ToolTip;

namespace AutoDefrag;

/// <summary>
/// 全局 ToolTip 生命周期守护：
/// 鼠标离开 / 点击 / 滚动 / 窗口失焦 / 窗口移动或缩放 / 控件被禁用或折叠 / 视图切换 时统一关闭，
/// 避免 ToolTip 的 Popup 因宿主元素移动、重建或窗口状态变化而留在屏幕上。
/// （1.6.0 起由 App.OnStartup 调用 Initialize；所有窗口共享同一套行为，不做单控件补丁。）
/// </summary>
public static class ToolTipGuard
{
    private static readonly object Gate = new();
    private static readonly HashSet<ToolTip> OpenTips = new();
    private static readonly Dictionary<Window, WindowHooks> WindowHooksMap = new();

    private static DispatcherTimer? _sweepTimer;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
            new RoutedEventHandler(OnToolTipOpened));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.ClosedEvent,
            new RoutedEventHandler(OnToolTipClosed));
        EventManager.RegisterClassHandler(typeof(ToolTip), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnToolTipMouseDown));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnWindowUnloaded));
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnElementUnloaded));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnScrollChanged));

        // 兜底巡检：事件漏网（例如宿主被禁用、面板折叠、窗口在后台）时最多 1 秒内关闭
        _sweepTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sweepTimer.Tick += (_, _) => Sweep();
        _sweepTimer.Start();
    }

    private static void OnToolTipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip tip) return;
        lock (Gate) OpenTips.Add(tip);
    }

    private static void OnToolTipClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip tip) return;
        lock (Gate) OpenTips.Remove(tip);
    }

    private static void OnToolTipMouseDown(object sender, MouseButtonEventArgs e) => CloseAll();

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 只关闭挂在“这次滚动”内容上的提示：日志框自动滚屏时不应把鼠标正悬停的按钮提示打断，
        // 否则会因反复关闭/重开而闪烁。被滚动带走位置的列表条目提示仍会立即关闭。
        if (sender is ScrollViewer scroller) CloseTipsWithin(scroller);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;
        lock (Gate)
        {
            if (WindowHooksMap.ContainsKey(window)) return;
            WindowHooksMap[window] = new WindowHooks(window);
        }
    }

    private static void OnWindowUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) DetachWindow(window);
    }

    private static void DetachWindow(Window window)
    {
        WindowHooks? hooks;
        lock (Gate)
        {
            if (!WindowHooksMap.TryGetValue(window, out hooks)) return;
            WindowHooksMap.Remove(window);
        }
        hooks.Detach();
    }

    /// <summary>宿主元素被移出可视树时，只关闭挂在这个元素（或其子树）上的 ToolTip。</summary>
    private static void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) CloseTipsWithin(element);
    }

    /// <summary>关闭 PlacementTarget 位于指定子树内的 ToolTip；其它位置的提示不受影响。</summary>
    private static void CloseTipsWithin(DependencyObject ancestor)
    {
        ToolTip[] tips;
        lock (Gate)
        {
            if (OpenTips.Count == 0) return;
            tips = OpenTips.ToArray();
        }

        foreach (var tip in tips)
        {
            if (!tip.IsOpen) continue;
            if (tip.PlacementTarget is DependencyObject target && IsWithin(target, ancestor))
            {
                tip.IsOpen = false;
            }
        }
    }

    private static bool IsWithin(DependencyObject target, DependencyObject ancestor)
    {
        for (var current = target; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current == ancestor) return true;
        }
        return false;
    }

    /// <summary>兜底巡检：宿主不可见、被禁用、不在活动窗口里时强制关闭。</summary>
    private static void Sweep()
    {
        ToolTip[] tips;
        lock (Gate)
        {
            if (OpenTips.Count == 0) return;
            tips = OpenTips.ToArray();
        }

        foreach (var tip in tips)
        {
            if (!tip.IsOpen) continue;
            if (!IsOwnerAlive(tip)) tip.IsOpen = false;
        }
    }

    private static bool IsOwnerAlive(ToolTip tip)
    {
        // ToolTipService 创建的 ToolTip 可能没有 PlacementTarget（Popup 跟随鼠标），
        // 这种情况下无法用宿主状态判断，交给 WPF 自身的 ShowDuration 与显式事件处理；
        // 不能因为“没有宿主”就误关正常悬浮的提示。
        if (tip.PlacementTarget is not FrameworkElement target) return true;
        if (!target.IsVisible || !target.IsEnabled) return false;

        var window = Window.GetWindow(target);
        if (window is null || !window.IsVisible) return false;
        if (window.WindowState == WindowState.Minimized) return false;
        if (!window.IsActive) return false;

        return PresentationSource.FromVisual(target) is not null;
    }

    private static void CloseAll()
    {
        ToolTip[] tips;
        lock (Gate)
        {
            if (OpenTips.Count == 0) return;
            tips = OpenTips.ToArray();
        }

        foreach (var tip in tips)
        {
            if (tip.IsOpen) tip.IsOpen = false;
        }
    }

    private sealed class WindowHooks
    {
        private readonly Window _window;

        public WindowHooks(Window window)
        {
            _window = window;
            window.Deactivated += HandleDeactivated;
            window.LocationChanged += HandleLocationChanged;
            window.SizeChanged += HandleSizeChanged;
            window.StateChanged += HandleStateChanged;
            window.PreviewMouseDown += HandlePreviewMouseDown;
            window.PreviewMouseWheel += HandlePreviewMouseWheel;
            window.Closed += HandleClosed;
        }

        public void Detach()
        {
            _window.Deactivated -= HandleDeactivated;
            _window.LocationChanged -= HandleLocationChanged;
            _window.SizeChanged -= HandleSizeChanged;
            _window.StateChanged -= HandleStateChanged;
            _window.PreviewMouseDown -= HandlePreviewMouseDown;
            _window.PreviewMouseWheel -= HandlePreviewMouseWheel;
            _window.Closed -= HandleClosed;
        }

        private void HandleDeactivated(object? sender, EventArgs e) => CloseAll();
        private void HandleLocationChanged(object? sender, EventArgs e) => CloseAll();
        private void HandleSizeChanged(object? sender, SizeChangedEventArgs e) => CloseAll();
        private void HandleStateChanged(object? sender, EventArgs e) => CloseAll();
        private void HandlePreviewMouseDown(object sender, MouseButtonEventArgs e) => CloseAll();
        private void HandlePreviewMouseWheel(object sender, MouseWheelEventArgs e) => CloseAll();

        private void HandleClosed(object? sender, EventArgs e)
        {
            Detach();
            lock (Gate) WindowHooksMap.Remove(_window);
        }
    }
}
