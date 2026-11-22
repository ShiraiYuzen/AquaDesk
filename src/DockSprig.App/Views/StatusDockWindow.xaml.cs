using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DockSprig.Core;
using DockSprig.Models;
using DockSprig.Services;

namespace DockSprig.Views;

/// <summary>
/// Dock 右侧的状态胶囊：托盘 / 电量 / WiFi / 蓝牙 / 音量 / 输入法。
///
/// 这些原来都在顶部菜单栏上，顶部改成只显示时间的浮动胶囊后搬到这里 ——
/// 底部一条 Dock 管应用，右边一小条管系统状态。
///
/// 位置跟着主 Dock 走（LocationChanged / SizeChanged 都重新贴一次），
/// 竖直方向与 Dock 玻璃条底边对齐，右侧留一个 Gap。
/// </summary>
public partial class StatusDockWindow : Window
{
    /// <summary>和 Dock 玻璃条之间的间距（DIP）。</summary>
    private const double Gap = 8;

    /// <summary>窗口上下给阴影留的白，对齐时要把这两截减掉。</summary>
    private const double ShadowTop = 6;
    private const double ShadowBottom = 10;

    private readonly AppSettings _settings;
    private readonly DockWindow _dock;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    /// <summary>输入法状态要快一点，挂 1 秒那个上会慢半拍。</summary>
    private readonly DispatcherTimer _inputTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };

    public StatusDockWindow(AppSettings settings, DockWindow dock)
    {
        InitializeComponent();

        _settings = settings;
        _dock = dock;

        // 风格（底色 / 边缘光）直接绑设置
        DataContext = settings;


        // 面板里改完音量 / 连上 Wi-Fi，胶囊上的图标要立刻跟上
        MenuPanel.VolumeChanged += _ => RefreshVolume();
        MenuPanel.WifiStateChanged += RefreshWifiIcon;

        MenuPanel.InputMethodChanged += () =>
        {
            ClosePanel();
            Dispatcher.BeginInvoke(RefreshInputMethod, DispatcherPriority.Background);
        };

        MenuPanel.RequestClose += ClosePanel;

        Loaded += OnLoaded;
        SizeChanged += (_, _) => FollowDock();
        Closed += OnClosed;

        // 主 Dock 动一下（图标增减、换停靠边、放大），胶囊跟着走
        _dock.LocationChanged += OnDockMoved;
        _dock.SizeChanged += OnDockMoved;
        _dock.IsVisibleChanged += OnDockVisibilityChanged;

        // 悬停放大 / 图标增减会让玻璃条自己变宽变高，窗口尺寸却不变 ——
        // 只听窗口事件的话，玻璃条涨过来照样压住胶囊
        _dock.GlassBarChanged += OnDockMoved;

        _timer.Tick += (_, _) => Refresh();
        _inputTimer.Tick += (_, _) => RefreshInputMethod();
    }

    /// <summary>
    /// 退订 Dock 的事件再关窗。
    /// 用 lambda 订阅是没法退订的，Dock 比胶囊活得久（胶囊可能被重建），
    /// 不退订就等于让 Dock 一直攥着已经关掉的窗口实例。
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _inputTimer.Stop();
        _panelWatch?.Stop();
        _panelWatch = null;

        _dock.LocationChanged -= OnDockMoved;
        _dock.SizeChanged -= OnDockMoved;
        _dock.IsVisibleChanged -= OnDockVisibilityChanged;
        _dock.GlassBarChanged -= OnDockMoved;
    }

    private void OnDockMoved(object? sender, EventArgs e) => FollowDock();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 点击不能抢键盘焦点：否则前台还是原来的应用，焦点却被我们拿走了，
        // 回到应用里打字没反应（详见 NonActivatingWindow 的注释）
        NonActivatingWindow.Apply(this);

        Refresh();
        RefreshInputMethod();

        FollowDock();

        _timer.IsEnabled = _dock.IsVisible;
        _inputTimer.IsEnabled = _dock.IsVisible;
    }

    /// <summary>Dock 藏起来的时候（托盘里切换显示），状态胶囊一起藏。</summary>
    private void SyncDockVisibility()
    {
        var visible = _dock.IsVisible;
        Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (!IsLoaded) return;
        _timer.IsEnabled = visible;
        _inputTimer.IsEnabled = visible;
        if (visible)
        {
            Refresh();
            RefreshInputMethod();
        }
    }

    private void OnDockVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e) => SyncDockVisibility();

    /// <summary>
    /// 贴到主 Dock 玻璃条的右侧，高度也取玻璃条的高度。
    ///
    /// 尺寸和位置一律按 <see cref="DockWindow.GlassBarBounds"/> 来 ——
    /// 玻璃条是跟着图标内容居中的，外面还套了「放大余量 + 内边距」几层换算，
    /// 用窗口矩形推出来的边至少差三十几像素（胶囊偏高、悬停时被压住都是这个原因）。
    /// </summary>
    private void FollowDock()
    {
        // RefreshLayout 会同步改 Dock 位置 → 又触发一次 LocationChanged。
        // 挡住这一层重入，否则会「按旧位置算一次、再按新位置算一次」，开机时胶囊肉眼可见地跳一下。
        if (_inFollow) return;
        if (ActualWidth <= 0) return;

        var bar = _dock.GlassBarBounds();
        if (bar.IsEmpty) return;

        _inFollow = true;
        try
        {
            // 高度 = 玻璃条高度 + 阴影留白。图标大小 / 界面缩放一变，两边一起变
            var wantedHeight = bar.Height + ShadowTop + ShadowBottom;
            if (Math.Abs(Height - wantedHeight) > 0.5) Height = wantedHeight;

            var wa = SystemParameters.WorkArea;

            // 先把 Dock 让出来：整组居中，不然 Dock 自己居中、胶囊吊在右边，视觉重心会歪
            var shift = (ActualWidth + Gap) / 2;
            if (Math.Abs(_dock.HorizontalShift - shift) > 0.5)
            {
                _dock.HorizontalShift = shift;
                _dock.RefreshLayout();
            }

            var left = bar.Right + Gap;

            // 右边放不下就翻到 Dock 左边
            if (left + ActualWidth > wa.Right - 4)
            {
                left = bar.Left - Gap - ActualWidth;
            }

            left = Math.Max(wa.Left + 4, left);

            // 胶囊的视觉边要压在玻璃条的边上：底部停靠时对齐底边、顶部停靠时对齐顶边。
            // 窗口比胶囊多留了一圈阴影白，所以这个方向上要补回来。
            var top = _settings.Edge == DockEdge.Top
                ? bar.Top - ShadowTop
                : bar.Bottom - ActualHeight + ShadowBottom;

            // 首次跟位时 Left / Top 还是 NaN（手动定位的窗口初值），NaN 参与比较永远为 false，
            // 所以得单独判一次，否则窗口会一直停在 0,0
            var moved = double.IsNaN(Left) || double.IsNaN(Top)
                        || Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5;

            if (moved)
            {
                App.Trace($"状态胶囊：{left:F0},{top:F0} {ActualWidth:F0}×{ActualHeight:F0}" +
                        $"（玻璃条 {bar.Left:F0},{bar.Top:F0} {bar.Width:F0}×{bar.Height:F0}）");

                Left = left;
                Top = top;

                // 悬停放大时 Dock 的玻璃条会往两侧长，两个都是置顶窗口，谁最后设置谁在上面。
                // 只有真的挪了位置才抬一次：每帧都设 SetWindowPos 会让胶囊自己重绘一遍。
                // 但若当前浮层处于「沉底」状态（前台窗口最大化 / 全屏），就不能再把它顶回去，
                // 否则会盖住 WPS 这类最大化窗口的内容。
                if (!OverlayZOrder.IsSunk)
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        NativeMethods.SetWindowPos(
                            hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                    }
                }
            }
        }
        finally
        {
            _inFollow = false;
        }
    }

    // ---------------- 状态刷新 ----------------

    private void Refresh()
    {
        RefreshBattery();
        RefreshVolume();

        // 查询本身走 4 秒缓存，每秒叫一次不会真的每次都去开 WLAN 句柄
        RefreshWifiIcon();
    }

    private void RefreshBattery()
    {
        var battery = SystemStatus.GetBattery();

        if (!battery.HasBattery)
        {
            BatteryText.Text = "电源";
            BatteryFill.Width = 16;
            BatteryFill.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
            ChargeBolt.Visibility = Visibility.Collapsed;
            return;
        }

        BatteryText.Text = $"{battery.Percent}%";

        // 电池内可用宽度 = 22 - 2(边框) - 4(左右 margin)
        BatteryFill.Width = Math.Max(0, 16 * battery.Percent / 100.0);

        // 电量条统一白色，只有低于 20% 才变红示警；充电靠电池里的闪电区分
        BatteryFill.Background = battery.Percent <= 20
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))
            : new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));

        ChargeBolt.Visibility = battery.IsCharging ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 连接状态 + 信号强度决定图标形态。
    /// 查询放到线程池：枚举接口偶尔会被驱动卡住几十毫秒，别把 UI 拖住。
    /// </summary>
    private void RefreshWifiIcon()
    {
        Task.Run(() => WifiService.Query()).ContinueWith(task =>
        {
            if (task.IsFaulted || task.IsCanceled) return;

            var snapshot = task.Result;
            var signal = snapshot.ConnectedSsid is null ? -1 : snapshot.Signal;

            Dispatcher.BeginInvoke(new Action(() => ApplyWifiIcon(signal)));
        }, TaskScheduler.Default);
    }

    private void ApplyWifiIcon(int signal)
    {
        if (signal < 0)
        {
            WifiWave3.Opacity = 0.22;
            WifiWave2.Opacity = 0.22;
            WifiDot.Opacity = 0.22;
            WifiSlash.Visibility = Visibility.Visible;
            return;
        }

        WifiSlash.Visibility = Visibility.Collapsed;
        WifiDot.Opacity = 1;
        WifiWave2.Opacity = signal >= 30 ? 1 : 0.22;
        WifiWave3.Opacity = signal >= 65 ? 1 : 0.22;
    }

    private void RefreshVolume()
    {
        var volume = SystemStatus.GetVolume();

        VolumeWave1.Opacity = volume > 0 ? 1.0 : 0.28;
        VolumeWave2.Opacity = volume > 45 ? 1.0 : 0.28;
    }

    /// <summary>
    /// 键盘布局指示：能取到输入法注册的图标就显示图标（转成单色白适配深色胶囊），
    /// 取不到（比如美式键盘）就退回「中 / EN」文字。
    /// </summary>
    private void RefreshInputMethod()
    {
        var badge = InputState.GetBadge();

        InputStateText.Text = badge ?? string.Empty;
        InputArea.Visibility = string.IsNullOrEmpty(badge) ? Visibility.Hidden : Visibility.Visible;

        var visual = InputMethodIcon.GetCurrent();

        if (visual.Icon is not null)
        {
            InputGlyph.Source = visual.Icon;
            InputGlyph.Visibility = Visibility.Visible;
            InputText.Visibility = Visibility.Collapsed;
            return;
        }

        InputGlyph.Visibility = Visibility.Collapsed;
        InputText.Visibility = Visibility.Visible;
        InputText.Text = visual.Label;
    }

    // ---------------- 点击 ----------------

    private void OnNetworkClick(object sender, MouseButtonEventArgs e) =>
        ShowPanel(WifiIcon, panel => panel.ShowWifi());

    private void OnBluetoothClick(object sender, MouseButtonEventArgs e) =>
        ShowPanel(BluetoothIcon, panel => panel.ShowBluetooth());

    private void OnVolumeClick(object sender, MouseButtonEventArgs e) =>
        ShowPanel(VolumeIcon, panel => panel.ShowVolume());

    private void OnBackgroundClick(object sender, MouseButtonEventArgs e) =>
        ShowPanel(Pill, panel => panel.ShowBackground());

    // 输入法状态和键盘布局拆成了两格，各自可点；锚点跟着被点的那个走
    private void OnInputClick(object sender, MouseButtonEventArgs e) =>
        ShowPanel(sender as FrameworkElement ?? Pill, panel => panel.ShowKeyboard());

    // ---------------- Dock 搬过来的两个入口 ----------------
    //
    // 原本在 Dock 最左边：四格 Windows 图标（点开电源菜单）和 AquaDesk 品牌图标（点开设置）。
    // 0.3.11 起 Dock 不再放这两个，改挂在状态胶囊上 —— 一条 Dock 只留应用，系统入口归胶囊。

    private void OnWindowsClick(object sender, MouseButtonEventArgs e)
    {
        // 面板开着就先收掉，否则电源菜单和下拉面板会叠在一起
        ClosePanel();

        if (TryFindResource("PowerMenu") is not ContextMenu menu) return;

        // 锚点用整条胶囊，位置用回调精确算：
        //   Placement=Top 配小图标时，菜单会以图标为中心、底边压进胶囊顶部（实测压 17px），
        //   胶囊高度又随图标尺寸变，静态 VerticalOffset 定不住。
        // 回调里直接拿到菜单和胶囊的真实尺寸：左边缘对齐胶囊左边缘，底边贴在胶囊顶上。
        menu.PlacementTarget = Pill;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            new[] { new CustomPopupPlacement(new Point(0, -popupSize.Height), PopupPrimaryAxis.Horizontal) };
        menu.IsOpen = true;
    }

    private void OnBrandClick(object sender, MouseButtonEventArgs e)
    {
        ClosePanel();

        // 交给 Dock 去开：设置窗口是 Dock 持有的单例，重复开会让位置/尺寸跳一下
        _dock.ShowSettings();
    }

    private void OnPowerLock(object sender, RoutedEventArgs e) => PowerService.Lock();

    private void OnPowerSleep(object sender, RoutedEventArgs e) => PowerService.Sleep();

    private void OnPowerShutdown(object sender, RoutedEventArgs e) => PowerService.Shutdown();

    private void OnPowerRestart(object sender, RoutedEventArgs e) => PowerService.Restart();

    // ---------------- 下拉面板（向上弹） ----------------

    private DispatcherTimer? _panelWatch;

    /// <summary>FollowDock 的重入锁。</summary>
    private bool _inFollow;

    private void ShowPanel(FrameworkElement anchor, Action<MenuBarPanel> configure)
    {
        ClosePanel();

        // 面板还没弹出，此刻前台还是用户正在用的应用 —— 先记下来。
        // 等面板打开、用户点面板里的行时，焦点已经被抢到面板 Popup 上，
        // 再现场读 GetForegroundWindow() 拿到的是我们自己（详见 KeyboardLayouts.CaptureSwitchTarget）。
        KeyboardLayouts.CaptureSwitchTarget();

        configure(MenuPanel);

        // 延后一帧再打开，等面板量出尺寸才好定位
        Dispatcher.BeginInvoke(() =>
        {
            PositionPanel(anchor);
            PanelPopup.IsOpen = true;
            StartPanelWatch();
            App.Log($"面板：open={PanelPopup.IsOpen} child={PanelPopup.Child?.GetType().Name} " +
                    $"size={(PanelPopup.Child as FrameworkElement)?.ActualWidth:F0}×{(PanelPopup.Child as FrameworkElement)?.ActualHeight:F0} " +
                    $"off={PanelPopup.HorizontalOffset:F0},{PanelPopup.VerticalOffset:F0} 可见={PanelPopup.Child?.IsVisible}", null);

            // 打开后真实高度才知道，把面板挪到胶囊正上方
            Dispatcher.BeginInvoke(() => RepositionAboveAnchor(anchor), DispatcherPriority.Loaded);

            // 第一次显示时 ActualWidth 还是 0，等布局好了再把音量条摆正
            Dispatcher.BeginInvoke(() => MenuPanel.RefreshVolumeLayout(), DispatcherPriority.Loaded);
        }, DispatcherPriority.Input);
    }

    private void ClosePanel()
    {
        _panelWatch?.Stop();
        _panelWatch = null;
        PanelPopup.IsOpen = false;

        // 面板关了就别再每 150ms 读一次音量
        MenuPanel.StopLiveUpdates();

        // 蓝牙搜索也要停，DeviceWatcher 一直在后台跑会耗电
        MenuPanel.StopBluetooth();
    }

    /// <summary>
    /// Popup 用 StaysOpen=True，自己盯着鼠标。
    /// 用 StaysOpen=False 的话，面板开着时点另一个图标，那一下会被当成「点外面」吃掉。
    /// </summary>
    private void StartPanelWatch()
    {
        _panelWatch?.Stop();

        _panelWatch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };

        _panelWatch.Tick += (_, _) =>
        {
            if (!PanelPopup.IsOpen)
            {
                ClosePanel();
                return;
            }

            var leftDown = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0;
            var rightDown = (NativeMethods.GetAsyncKeyState(0x02) & 0x8000) != 0;

            if (!leftDown && !rightDown) return;
            if (NativeMethods.GetCursorPos(out var point) && IsInsidePanel(point)) return;

            ClosePanel();
        };

        _panelWatch.Start();
    }

    private bool IsInsidePanel(NativeMethods.POINT screenPoint)
    {
        if (PanelPopup.Child is not FrameworkElement child || child.ActualWidth <= 0) return false;

        var topLeft = child.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(child);

        var rect = new Rect(topLeft.X, topLeft.Y,
            child.ActualWidth * dpi.DpiScaleX,
            child.ActualHeight * dpi.DpiScaleY);

        return rect.Contains(screenPoint.X, screenPoint.Y);
    }

    private void PositionPanel(FrameworkElement anchor)
    {
        const double panelWidth = 334;
        const double margin = 8;

        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = anchor.PointToScreen(new Point(0, 0));

        // 面板右边缘对齐图标右边缘，同时不越出屏幕
        var anchorRight = screen.X / dpi.DpiScaleX + anchor.ActualWidth;
        var left = Math.Clamp(anchorRight - panelWidth, margin, SystemParameters.PrimaryScreenWidth - panelWidth - margin);

        PanelPopup.HorizontalOffset = left;

        // 先给个保守估计（面板一般 300~500 高），真实高度在 Loaded 后修正
        PanelPopup.VerticalOffset = Math.Max(margin, Top - 420);
    }

    /// <summary>面板底边贴着胶囊顶边往上长。</summary>
    private void RepositionAboveAnchor(FrameworkElement anchor)
    {
        if (PanelPopup.Child is not FrameworkElement child) return;

        var height = child.ActualHeight;
        if (height <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var anchorTop = anchor.PointToScreen(new Point(0, 0)).Y / dpi.DpiScaleY;

        PanelPopup.VerticalOffset = Math.Max(8, anchorTop - height - 10);
    }
}
