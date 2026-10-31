using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AquaDesk.Core;
using AquaDesk.Services;

namespace AquaDesk.Views;

/// <summary>
/// 顶部菜单栏：左边当前应用名，右边电量 / WiFi / 音量 / 输入法 / 时钟。
/// 每秒刷一次，窗口本身不吃焦点（ShowActivated=False）。
/// </summary>
public partial class MenuBarWindow : Window
{
    /// <summary>菜单栏高度（DIP）。跟着界面缩放走，2K 屏上会高一点。</summary>
    private readonly double _barHeight;

    private const double BaseBarHeight = 26;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    /// <summary>
    /// 输入法状态单独用一个快一点的定时器。
    /// 挂在 1 秒那个上会「慢半拍」：切中英文、切键盘布局后要等下一次 tick 才刷新，
    /// 手感上就是明显延迟。
    /// </summary>
    private readonly DispatcherTimer _inputTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };

    private string _lastProcess = string.Empty;

    public MenuBarWindow(AquaDesk.Models.AppSettings settings)
    {
        InitializeComponent();

        _barHeight = Math.Round(BaseBarHeight * settings.UiScale);

        // 面板里改完音量 / 连上 Wi-Fi，菜单栏图标要立刻跟上，
        // 不然得等下一次定时器（音量 1 秒、Wi-Fi 4 秒）才有反应
        MenuPanel.VolumeChanged += _ => RefreshVolume();
        MenuPanel.WifiStateChanged += RefreshWifiIcon;

        // 在键盘布局下拉框里选完，收面板 + 刷新指示（切换是发给前台窗口的，要等它处理完）
        MenuPanel.InputMethodChanged += () =>
        {
            ClosePanel();
            Dispatcher.BeginInvoke(RefreshInputMethod, DispatcherPriority.Background);
        };

        MenuPanel.RequestClose += ClosePanel;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            _inputTimer.Stop();

            // 撤掉顶部预留，不然桌面会一直缺一条
            AppBarService.Remove();
        };

        _timer.Tick += (_, _) => Refresh();
        _inputTimer.Tick += (_, _) => RefreshInputMethod();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = _barHeight;

        ReserveTopSpace();

        RefreshWifiIcon();
        Refresh();
        RefreshInputMethod();

        _timer.Start();
        _inputTimer.Start();

        // AppBar 注册完 Shell 会把窗口挪到预留区的底部（Top 变成 26），
        // 而 WPF 里 Top 已经是 0，再赋 0 是空操作、改不回来 —— 只能用 SetWindowPos 硬钉。
        //
        // 而且不止启动时会挪：在设置里切换「隐藏任务栏」会让工作区重算，Shell 又挪一次。
        // 所以挂一个 1 秒的守护，发现不在 (0,0) 就重新钉 —— 一次 GetWindowRect，几乎不花钱。
        Dispatcher.BeginInvoke(new Action(EnsurePinnedToTop), DispatcherPriority.ApplicationIdle);

        _pinWatch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pinWatch.Tick += (_, _) => EnsurePinnedToTop();
        _pinWatch.Start();
    }

    private DispatcherTimer? _pinWatch;

    /// <summary>不在 (0,0) 就重新钉。已经是正位就什么都不做。</summary>
    private void EnsurePinnedToTop()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);

        if (NativeMethods.GetWindowRect(hwnd, out var rect)
            && rect.Left == 0 && rect.Top == 0 && rect.Right - rect.Left == width)
        {
            return;
        }

        PinToScreenTop();
    }

    /// <summary>把窗口硬钉到屏幕最顶部（绕过 WPF 的 Top 空操作问题）。</summary>
    private void PinToScreenTop()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);

        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // 用 Win32 的真实矩形来验证（WPF 的 Top 不会跟着外部移动更新）
        var actual = NativeMethods.GetWindowRect(hwnd, out var r) ? $"{r.Left},{r.Top} {r.Right - r.Left}×{r.Bottom - r.Top}" : "读不到";
        App.Log($"菜单栏重新钉顶：Win32 实际 {actual}", null);
    }

    /// <summary>
    /// 把顶部这 26px 占成 AppBar，最大化窗口就会从菜单栏下方开始，不会钻到它底下。
    /// Dock 那边不做这件事 —— Dock 是要浮在窗口上面的。
    /// </summary>
    private void ReserveTopSpace()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);

        if (!AppBarService.Register(hwnd))
        {
            App.Log("菜单栏 AppBar 注册失败，最大化窗口会盖住菜单栏", null);
            return;
        }

        var rect = AppBarService.ReserveTop(_barHeight, dpi.DpiScaleX, dpi.DpiScaleY);

        // 任务栏也在顶部时 Shell 会把我们挪下去，这里跟着走
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;

        App.Log($"菜单栏 AppBar：批下来 {rect.Left},{rect.Top} {rect.Width}×{rect.Height}（DPI {dpi.DpiScaleX:F2}）", null);
    }

    private void Refresh()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");

        RefreshBattery();
        RefreshVolume();

        // 查询本身走 4 秒缓存，每秒叫一次不会真的每次都去开 WLAN 句柄
        RefreshWifiIcon();

        var process = SystemStatus.GetForegroundAppName();
        if (process != _lastProcess)
        {
            _lastProcess = process;
            AppNameText.Text = FriendlyName(process);
        }
    }

    private void RefreshBattery()
    {
        var battery = SystemStatus.GetBattery();

        if (!battery.HasBattery)
        {
            BatteryText.Text = "电源";
            BatteryFill.Width = 18;
            BatteryFill.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
            ChargeBolt.Visibility = Visibility.Collapsed;
            return;
        }

        BatteryText.Text = $"{battery.Percent}%";

        // 电池内可用宽度 = 24 - 2(边框) - 4(左右 margin)
        BatteryFill.Width = Math.Max(0, 18 * battery.Percent / 100.0);

        // 电量条统一白色，只有低于 20% 才变红示警；充电靠电池里的闪电区分
        BatteryFill.Background = battery.Percent <= 20
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))
            : new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));

        ChargeBolt.Visibility = battery.IsCharging ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 连接状态 + 信号强度决定图标形态。
    /// 查询放到线程池：wlanapi 本身很快，但枚举接口偶尔会被驱动卡住几十毫秒，
    /// 直接在定时器里同步调会把整条菜单栏拖住。
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
            // 未连接：三格全部压暗，再划一道斜线
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

    /// <summary>把进程名换成好看点的显示名。</summary>
    private static string FriendlyName(string process) => process.ToLowerInvariant() switch
    {
        "explorer" => "访达",
        "applicationframehost" => "系统",
        "aquadesk" => "AquaDesk",
        "launchpadwindow" => "启动台",
        _ => char.ToUpperInvariant(process[0]) + process[1..]
    };

    // ---------------- 点击 ----------------

    private void OnBrandClick(object sender, MouseButtonEventArgs e)
    {
        if (Application.Current.MainWindow is DockWindow dock) dock.ShowSettings();
    }

    // ---------------- 下拉面板 ----------------

    private void OnNetworkClick(object sender, MouseButtonEventArgs e) =>
        ShowPanel(WifiIcon, panel => panel.ShowWifi());

    private void OnTrayClick(object sender, MouseButtonEventArgs e) =>
        TrayService.OpenOverflow();

    private void OnBluetoothClick(object sender, MouseButtonEventArgs e) =>
        ShowPanel(BluetoothIcon, panel => panel.ShowBluetooth());

    private void OnVolumeClick(object sender, MouseButtonEventArgs e) =>
        ShowPanel(VolumeIcon, panel => panel.ShowVolume());

    private System.Windows.Threading.DispatcherTimer? _panelWatch;

    private void ShowPanel(FrameworkElement anchor, Action<MenuBarPanel> configure)
    {
        ClosePanel();
        configure(MenuPanel);

        // 延后一帧再打开，等面板量出尺寸才好定位
        Dispatcher.BeginInvoke(() =>
        {
            PositionPanel(anchor);
            PanelPopup.IsOpen = true;
            StartPanelWatch();

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
    /// 用 StaysOpen=False 的话，面板开着时点另一个图标，那一下会被当成「点外面」吃掉，
    /// 得点两次才切得过去。
    /// </summary>
    private void StartPanelWatch()
    {
        _panelWatch?.Stop();

        _panelWatch = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
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
        PanelPopup.VerticalOffset = _barHeight;
    }

    /// <summary>
    /// 菜单栏上的键盘布局指示：能取到输入法注册的图标就显示图标（转成单色白适配深色栏），
    /// 取不到（比如美式键盘）就退回「中 / EN」文字。
    /// </summary>
    private void RefreshInputMethod()
    {
        // 左边那个小标记：中 / 英 / A；英文键盘返回 null，直接把控件收起来
        var badge = InputState.GetBadge();

        if (string.IsNullOrEmpty(badge))
        {
            InputStateText.Visibility = Visibility.Collapsed;
        }
        else
        {
            InputStateText.Text = badge;
            InputStateText.Visibility = Visibility.Visible;
        }

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

    private void OnInputClick(object sender, MouseButtonEventArgs e) =>
        ShowPanel(InputArea, panel => panel.ShowKeyboard());

    private static void Shell(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 打开 {target} 失败：{ex.Message}");
        }
    }
}
