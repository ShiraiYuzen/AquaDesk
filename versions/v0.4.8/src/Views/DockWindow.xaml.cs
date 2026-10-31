using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AquaDesk.Core;
using AquaDesk.Models;
using AquaDesk.Services;
using AquaDesk.ViewModels;

namespace AquaDesk.Views;

public partial class DockWindow : Window
{
    private readonly DockViewModel _vm;

    /// <summary>每个图标一套变换：缩放（以底边中点为锚）+ 横向让位位移。</summary>
    private readonly Dictionary<DockItem, (ScaleTransform Scale, TranslateTransform Shift)> _transforms = new();

    /// <summary>上一帧因为放大而多出来的总宽度，用来把指针坐标换算回「未放大」的参照系。</summary>
    private double _lastTotalExtra;

    private SettingsWindow? _settingsWindow;

    private DispatcherTimer? _runningTimer;

    // 玻璃折射（抓屏与形变都在后台线程，UI 线程只负责把结果贴上去）
    private readonly GlassRefractor _refractor = new();
    private GlassGeometry? _glassGeometry;
    private Thread? _glassWorker;
    private volatile bool _glassRunning;
    private byte[]? _captureBuffer;
    private WriteableBitmap? _refractionBitmap;
    private int _refractionWidth;
    private int _refractionHeight;

    /// <summary>后台线程每帧要用到的几何参数，全部在 UI 线程算好后快照下来。</summary>
    private sealed record GlassGeometry(
        int X, int Y, int Width, int Height,
        int OutputWidth, int OutputHeight,
        int Step, int Padding,
        double Radius, double Edge, double Push,
        double DpiScale);

    public DockWindow(AppSettings settings)
    {
        _vm = new DockViewModel(settings);

        InitializeComponent();
        DataContext = _vm;

        Loaded += OnLoaded;
        SizeChanged += (_, _) =>
        {
            PositionDock();
            UpdateGlassGeometry();
        };

        // 玻璃条自己的尺寸变化（悬停放大、图标增减、换停靠边）——
        // 状态胶囊靠这个贴边，不然放大时玻璃条会涨过去把它压住。
        GlassRoot.SizeChanged += (_, _) =>
        {
            App.Trace($"玻璃条：{GlassRoot.ActualWidth:F0}×{GlassRoot.ActualHeight:F0}");
            GlassBarChanged?.Invoke(this, EventArgs.Empty);
        };
        LocationChanged += (_, _) => UpdateGlassGeometry();
        Closed += (_, _) =>
        {
            _glassRunning = false;
            _dragOverlay?.Close();
        };

        // 拖动排序 / 拖出移除
        // 用 Preview 版本：隧道事件从窗口往下走，不会被某个子元素标记 Handled 吃掉
        PreviewMouseMove += OnWindowMouseMove;
        MouseUp += OnWindowMouseUp;

        // 从资源管理器 / 桌面直接把文件拖进来
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;

        settings.PropertyChanged += OnSettingsChanged;

        // 排序 / 增删之后 ItemsControl 可能重建容器，之前缓存的那对变换就挂空了，
        // 不清掉的话这些图标会彻底失去放大效果。清掉后下一帧会自动重新挂上。
        _vm.Items.CollectionChanged += (_, _) => _transforms.Clear();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateEmptyState();
        UpdateWindowWidth();
        ApplyNonActivatingStyle();
        PositionDock();
        ApplyStyle();
        StartRunningWatcher();
        StartPauseWatcher();
        StartWindowPreviewWatcher();
    }

    // ---------------- 窗口预览 ----------------

    // ---------------- 窗口预览 ----------------

    private void OnActivateWindow(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuItemData(sender) is { } item) WindowPreviewService.Activate(item.WindowHandle);
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuItemData(sender) is not { } item) return;

        if (!WindowPreviewService.Close(item.WindowHandle))
        {
            App.Log($"窗口预览：关闭失败（可能是提权窗口）hwnd={item.WindowHandle}", null);
        }
    }

    /// <summary>
    /// 拖出 Dock 时把对应的系统图标开关关掉。改设置会触发 OnSettingsChanged，
    /// 那里会 RefreshStructure + 重算宽度，所以这里不用自己动 Items。
    /// </summary>
    private void TurnOffSystemIcon(DockItem item)
    {
        var settings = _vm.Settings;

        switch (item.Kind)
        {
            case DockItemKind.Brand:
                settings.ShowBrandIcon = false;
                break;

            case DockItemKind.Windows:
                settings.ShowWindowsIcon = false;
                break;

            case DockItemKind.Launchpad:
                settings.ShowLaunchpadIcon = false;
                break;

            case DockItemKind.Computer:
                settings.ShowComputerIcon = false;
                break;
        }
    }

    /// <summary>
    /// 提权窗口（任务管理器那种）关不掉 —— UIPI 会静默拦掉 WM_CLOSE。
    /// 与其让用户点了没反应，不如把菜单项直接置灰并说明原因。
    /// </summary>
    private static void UpdateWindowPreviewMenu(ContextMenu menu)
    {
        if (menu.PlacementTarget is not FrameworkElement { DataContext: DockItem { IsWindowPreview: true } preview })
        {
            return;
        }

        var canClose = WindowPreviewService.CanControl(preview.WindowHandle);

        foreach (var entry in menu.Items)
        {
            if (entry is not MenuItem { Tag: "close" } closeItem) continue;

            closeItem.IsEnabled = canClose;
            closeItem.Header = canClose ? "关闭窗口" : "关闭窗口（需要管理员权限）";
        }
    }

    /// <summary>缩略图抓成 96×60，贴到 52×34 的槽位里，2 倍屏也够清晰。</summary>
    private const int ThumbWidth = 96;
    private const int ThumbHeight = 60;

    private DispatcherTimer? _previewTimer;
    private bool _capturing;

    private void StartWindowPreviewWatcher()
    {
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(2000)
        };

        _previewTimer.Tick += (_, _) => RefreshWindowPreviews();
        _previewTimer.Start();

        RefreshWindowPreviews();
    }

    private void RefreshWindowPreviews()
    {
        if (_paused) return;

        var before = _vm.Items.Count;

        if (_vm.SyncWindowPreviews(WindowPreviewService.Enumerate()))
        {
            // 窗口集合变了，之前抓不出来的那些再试一次（可能之前只是被遮挡）
            WindowPreviewService.ResetFailureCache();
        }

        if (_vm.Items.Count != before)
        {
            UpdateWindowWidth();
            Dispatcher.BeginInvoke(PositionDock, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        CaptureThumbnails();
    }

    /// <summary>
    /// 抓缩略图放后台线程：PrintWindow 一个 1080p 窗口要十几毫秒，
    /// 六个窗口全在 UI 线程上做会明显卡一下。
    /// 鼠标不在 Dock 上时只补新出现的，不整体重抓 —— 小图看不出是不是实时的，没必要每 2 秒烧一次 CPU。
    /// </summary>
    private void CaptureThumbnails()
    {
        if (_capturing) return;

        var hovering = IsMouseOver;

        var targets = new List<(DockItem Item, IntPtr Handle)>();

        foreach (var preview in _vm.Previews)
        {
            if (preview.Thumbnail is null || hovering) targets.Add((preview, preview.WindowHandle));
        }

        if (targets.Count == 0) return;

        _capturing = true;

        Task.Run(() =>
        {
            var shots = new List<(DockItem Item, ImageSource Image)>();

            try
            {
                foreach (var (item, handle) in targets)
                {
                    var image = WindowPreviewService.Capture(handle, ThumbWidth, ThumbHeight);
                    if (image is not null) shots.Add((item, image));
                }
            }
            finally
            {
                _capturing = false;
            }

            if (shots.Count == 0) return;

            Dispatcher.BeginInvoke(() =>
            {
                foreach (var (item, image) in shots) item.Thumbnail = image;
            });
        });
    }

    /// <summary>定时扫进程和前台窗口，更新每个图标下方的运行指示条。</summary>
    private void StartRunningWatcher()
    {
        _vm.RefreshStates();

        _runningTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 800ms：进程扫描本身很便宜，太快反而浪费；这个频率切窗口已经跟得上了
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _runningTimer.Tick += (_, _) => _vm.RefreshStates();
        _runningTimer.Start();
    }

    /// <summary>按当前风格决定要不要开折射。经典风格连抓屏都不做，CPU 占用回到 0。</summary>
    private void ApplyStyle()
    {
        // 抓屏折射已停用（原因见 StartRefraction），现在只负责把排除标记清干净，
        // 保证三种外观下 Dock 都能正常出现在截图和录屏里。
        StopRefraction();
        RefractionLayer.Visibility = Visibility.Collapsed;

        // 流动曲面只在液态透明玻璃下跑
        if (FindResource("FlowStoryboard") is Storyboard flow)
        {
            if (_vm.Settings.IsLiquidClear) flow.Begin(this, true);
            else flow.Stop(this);
        }
    }

    // ---------------- 按条件暂停后台工作 ----------------

    private DispatcherTimer? _zTimer;
    private DispatcherTimer? _pauseTimer;
    private bool _paused;

    /// <summary>
    /// 两个计时器分工，频率差一个数量级：
    /// <list type="bullet">
    /// <item><c>_zTimer</c> —— 120ms，只管浮层 z 序。用户眼睛盯着看，必须跟手。</item>
    /// <item><c>_pauseTimer</c> —— 1200ms，暂停策略这类重活，慢一点没人察觉。</item>
    /// </list>
    /// </summary>
    private void StartPauseWatcher()
    {
        // Loaded 在窗口被摘出视觉树再挂回来时会重跑，重复建计时器就是泄漏
        if (_zTimer is not null) return;

        // z 序判断只查前台窗口的几个属性：GetForegroundWindow / IsZoomed /
        // GetWindowRect / GetMonitorInfo，全是 user32 调用，一轮十几微秒。
        // 120ms 跑一次 ≈ 每秒 8 次，CPU 上看不出来。
        //
        // 优先级必须是 Normal，不能是 Background —— 最大化那一瞬间 DWM 和 WPF
        // 正在重绘，队列里全堆着 Normal 级的渲染工作，Background 级的 tick 会被
        // 一直往后推，实测能拖到远超标称间隔，那才是「Dock 让开得慢」的主因。
        _zTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };

        _zTimer.Tick += (_, _) => EvaluateZOrder();
        _zTimer.Start();

        _pauseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };

        _pauseTimer.Tick += (_, _) => EvaluatePause();
        _pauseTimer.Start();
    }

    private void EvaluateZOrder()
    {
        var sink = _vm.Settings.TopMode switch
        {
            // 只在「真的全屏」时让开：最大化是普通工作状态，Dock 按用户要求留在上面
            DockTopMode.AlwaysTop => WindowQuery.IsForegroundFullscreen(out _),
            DockTopMode.AlwaysBottom => true,
            _ => WindowQuery.IsForegroundCoveringScreen()
        };

        OverlayZOrder.SetSunk(sink);
    }

    /// <summary>启动台 / 文件夹窗口开着时，浮层别抢置顶（它们自己会让位）。</summary>
    public bool IsTemporaryTopmostOpen => _launchpad is not null || _folderWindow is not null;

    private void EvaluatePause()
    {
        var shouldPause = _vm.Settings.PauseMode switch
        {
            DockPauseMode.Never => false,

            // 鼠标停在 Dock 上就算「在用」，立刻恢复，否则看着像卡死了
            DockPauseMode.Unfocused =>
                !IsMouseOver && !WindowQuery.IsOwnProcessForeground(),

            DockPauseMode.BorderlessFullscreen =>
                WindowQuery.IsForegroundFullscreen(out var borderless) && borderless,

            DockPauseMode.Fullscreen =>
                WindowQuery.IsForegroundFullscreen(out _),

            _ => false
        };

        if (shouldPause == _paused) return;
        _paused = shouldPause;

        if (_paused) PauseBackgroundWork();
        else ResumeBackgroundWork();
    }

    /// <summary>停折射线程、停状态轮询、把大缓冲丢掉，最后把工作集还给系统。</summary>
    private void PauseBackgroundWork()
    {
        StopRefraction();

        _runningTimer?.Stop();

        // 抓屏缓冲和折射位图是按窗口尺寸算的，通常几百 KB 到 1MB，直接丢掉最干脆
        _captureBuffer = null;
        _refractionBitmap = null;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            NativeMethods.SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 回收工作集失败：{ex.Message}");
        }
    }

    private void ResumeBackgroundWork()
    {
        ApplyStyle();
        _runningTimer?.Start();
        _vm.RefreshStates();

        // 暂停期间窗口可能开了又关，回来先对一次
        RefreshWindowPreviews();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 这里只负责「立刻生效」，不再顺手写盘 —— 保存交给设置页底部的「保存」按钮
        // （关窗时会兜底保存一次，见 SettingsWindow.OnClosing）。改动还是实时作用到界面上的。

        if (e.PropertyName is nameof(AppSettings.CornerRadius))
        {
            UpdateGlassGeometry();
        }

        if (e.PropertyName is nameof(AppSettings.Style))
        {
            ApplyStyle();
            ThemeManager.Apply(_vm.Settings.Style);
        }

        // 改了暂停策略，立刻按新策略重算一次，不用等下一个轮询周期
        if (e.PropertyName is nameof(AppSettings.PauseMode))
        {
            EvaluatePause();
        }

        // 换了窗口层级，立刻按新策略重算一次，不用等下一个轮询周期
        if (e.PropertyName is nameof(AppSettings.TopMode))
        {
            EvaluateZOrder();
        }

        // 窗口预览开关：开→立刻抓一轮，关→马上撤掉那一排
        if (e.PropertyName is nameof(AppSettings.ShowWindowPreviews))
        {
            RefreshWindowPreviews();
        }

        // 切换隐藏任务栏会改工作区（Shell 异步生效），稍等一下再贴底，否则会停在旧位置
        if (e.PropertyName is nameof(AppSettings.HideTaskbar))
        {
            ScheduleRelayout();
        }

        // 换停靠边：横竖变了，尺寸和位置都要重算
        if (e.PropertyName is nameof(AppSettings.Edge))
        {
            ResetMagnification();
            RefreshLayout();
        }

        // 系统图标显隐：整行重建，宽度和位置都得跟着重算
        if (e.PropertyName is nameof(AppSettings.ShowBrandIcon)
            or nameof(AppSettings.ShowWindowsIcon)
            or nameof(AppSettings.ShowLaunchpadIcon)
            or nameof(AppSettings.ShowTrashIcon)
            or nameof(AppSettings.ShowComputerIcon))
        {
            _vm.RefreshStructure();
            UpdateEmptyState();
            UpdateWindowWidth();
            Dispatcher.BeginInvoke(PositionDock, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (e.PropertyName is nameof(AppSettings.WindowHeight)
            or nameof(AppSettings.IconSize)
            or nameof(AppSettings.UiScale)
            or nameof(AppSettings.IconGap)
            or nameof(AppSettings.BottomMargin))
        {
            Height = _vm.Settings.WindowHeight;
            UpdateWindowWidth();
            Dispatcher.BeginInvoke(PositionDock, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void UpdateEmptyState()
    {
        // 空的时候玻璃条也要留着，不然 + 按钮就点不到了
        EmptyHint.Visibility = _vm.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 窗口比玻璃条宽两倍 ExpandReserve —— 放大时玻璃条要往外长，
    /// 窗口提前留好空位，就不用每帧改窗口宽度（那会抖）。
    /// </summary>
    private void UpdateWindowWidth()
    {
        var s = _vm.Settings;

        // 直接数 Items：图标宽的算一个磁贴，分隔线窄一些。
        // 这样加了窗口预览区、或者关掉某个系统图标，都不用再改公式。
        var tiles = 0;
        var separators = 0;

        foreach (var item in _vm.Items)
        {
            if (item.IsSeparator) separators++;
            else tiles++;
        }

        var content = s.TileSlotWidth * tiles + (13 + s.IconGap) * separators;

        var length = content + s.SidePadding * 2 + s.ExpandReserve * 2;

        // 厚度要把间距加回来：玻璃条是「贴边 + 让出 BottomMargin」摆的，
        // 窗口厚度不加这一截，间距一调大可用空间就被压缩，图标会被窗口裁掉。
        var thickness = s.WindowHeight + s.BottomMargin;

        if (s.IsVertical)
        {
            Width = thickness;
            Height = length;
        }
        else
        {
            Width = length;
            Height = thickness;
        }
    }

    /// <summary>
    /// 整体左移多少（DIP）。Dock 右侧挂了一条状态胶囊，由它来设置这个值，
    /// 让「Dock + 胶囊」作为一个整体居中，而不是 Dock 自己居中、胶囊吊在右边。
    /// </summary>
    public double HorizontalShift { get; set; }

    /// <summary>玻璃条的尺寸变了（悬停放大 / 图标增减 / 换停靠边 / 改缩放）。</summary>
    public event EventHandler? GlassBarChanged;

    /// <summary>
    /// 玻璃条在屏幕上的实际矩形（DIP）。
    ///
    /// 不能拿窗口矩形来推算：窗口高度是「玻璃条 + 放大余量」，水平方向还多留了两份
    /// ExpandReserve，而玻璃条本身是跟着图标内容居中的 —— 外面套了几层换算之后，
    /// 算出来的边和实际的边差出几十像素（状态胶囊就是这么被撑高、又被压住的）。
    /// 直接问 WPF 要玻璃条自己的矩形，位置和高度都准。
    /// </summary>
    public Rect GlassBarBounds()
    {
        if (!IsLoaded || GlassRoot.ActualWidth <= 0 || GlassRoot.ActualHeight <= 0) return Rect.Empty;

        var origin = GlassRoot.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);

        return new Rect(
            origin.X / dpi.DpiScaleX,
            origin.Y / dpi.DpiScaleY,
            GlassRoot.ActualWidth,
            GlassRoot.ActualHeight);
    }

    /// <summary>
    /// 贴底部居中。
    ///
    /// **不占工作区**：Dock 是浮层，最大化窗口应该铺满整块屏幕（顶到菜单栏、底到屏幕边），
    /// Dock 和菜单栏都盖在它上面 —— 和 macOS 一样。所以这里只定位，不做任何 AppBar 预留。
    /// 用 WorkArea 而不是屏幕尺寸：任务栏开着时 Dock 要贴在任务栏上沿，别叠上去。
    /// </summary>
    private void PositionDock()
    {
        if (double.IsNaN(ActualWidth) || ActualWidth <= 0) return;

        var wa = SystemParameters.WorkArea;

        switch (_vm.Settings.Edge)
        {
            case DockEdge.Top:
                Left = wa.Left + (wa.Width - ActualWidth) / 2 - HorizontalShift;
                Top = wa.Top;
                break;

            case DockEdge.Left:
                Left = wa.Left;
                Top = wa.Top + (wa.Height - ActualHeight) / 2;
                break;

            case DockEdge.Right:
                Left = wa.Right - ActualWidth;
                Top = wa.Top + (wa.Height - ActualHeight) / 2;
                break;

            default:
                Left = wa.Left + (wa.Width - ActualWidth) / 2 - HorizontalShift;
                Top = wa.Bottom - ActualHeight;
                break;
        }

        // 布局排查用：一眼看出工作区被谁啃掉了一块（高频，加 --verbose 才落盘）
        App.Trace($"布局：屏幕 {SystemParameters.PrimaryScreenWidth}×{SystemParameters.PrimaryScreenHeight}，" +
                $"工作区 {wa.Left},{wa.Top} {wa.Width}×{wa.Height}（底 {wa.Bottom}），" +
                $"Dock Top={Top:F1} Height={ActualHeight:F1}");
    }

    /// <summary>
    /// 关键一步：NOACTIVATE 让 Dock 点下去不抢焦点（否则点图标会把当前窗口弄失焦），
    /// TOOLWINDOW 让它不出现在 Alt+Tab 里。
    /// 另外要挡掉 WM_MOUSEACTIVATE —— 只设扩展样式的话，WPF 仍会在鼠标按下时
    /// 把键盘焦点挪到 Dock 上，用户回应用里打字就没反应了。
    /// </summary>
    private void ApplyNonActivatingStyle() => NonActivatingWindow.Apply(this);

    // ---------------- 放大 + 邻居让位 ----------------

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        UpdatePointerGlow(e);

        var count = _vm.Items.Count;
        if (count == 0)
        {
            DockItems.Width = double.NaN;
            DockItems.Height = double.NaN;
            return;
        }

        if (DockItems.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement first)
            return;

        // 以 StackPanel 自身为坐标原点：它会被居中对齐到变宽的 ItemsControl 里，
        // 用 ItemsControl 当参照系的话每帧位置都在漂。
        if (VisualTreeHelper.GetParent(first) is not FrameworkElement panel)
            return;

        // ItemsControl 变宽时整行会往左挪 totalExtra/2（居中生长），
        // 指针相对 panel 的坐标也跟着多出同样的量。不减掉这一项，
        // 放大 → 变宽 → 坐标右移 → 再放大，就会自激成抖动。
        var vertical = _vm.Settings.IsVertical;
        var pointer = (vertical ? e.GetPosition(panel).Y : e.GetPosition(panel).X) - _lastTotalExtra / 2;
        var boost = _vm.Settings.MagnifyMaxScale - 1;
        var sigma = _vm.Settings.MagnifySigma;
        var denominator = 2 * sigma * sigma;

        var scales = new double[count];
        var slots = new double[count];
        var extra = new double[count];
        var natural = 0.0;
        var totalExtra = 0.0;

        for (var i = 0; i < count; i++)
        {
            if (DockItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                return;

            slots[i] = vertical ? container.ActualHeight : container.ActualWidth;
            natural += slots[i];

            // 分隔线不参与放大：一根线被拉粗拉长很怪
            if (_vm.Items[i].IsSeparator)
            {
                scales[i] = 1;
                extra[i] = 0;
                continue;
            }

            var topLeft = container.TranslatePoint(new Point(0, 0), panel);
            var center = vertical
                ? topLeft.Y + container.ActualHeight / 2
                : topLeft.X + container.ActualWidth / 2;

            var distance = pointer - center;

            // 高斯衰减：正下方最大，往两边平滑收敛到 1.0
            scales[i] = 1 + boost * Math.Exp(-(distance * distance) / denominator);
            extra[i] = (scales[i] - 1) * slots[i];
            totalExtra += extra[i];
        }

        // 玻璃条按放大后的总宽度撑开（面板居中，所以左右各长一半）。
        // 加一点阈值：宽度只差零点几像素时不去动它，避免每帧都触发一轮重排。
        var targetLength = natural + totalExtra;

        if (vertical)
        {
            if (double.IsNaN(DockItems.Height) || Math.Abs(DockItems.Height - targetLength) > 0.5)
            {
                DockItems.Height = targetLength;
            }
        }
        else if (double.IsNaN(DockItems.Width) || Math.Abs(DockItems.Width - targetLength) > 0.5)
        {
            DockItems.Width = targetLength;
        }

        _lastTotalExtra = totalExtra;

        // 让位：第 i 个图标的排版左边 = 它前面所有图标挤出来的宽度 + 自己半个外扩量。
        // 缩放绕磁贴中心做，绘制框左右各外扩 extra/2，所以排版左边要再补 extra/2，
        // 这样第 0 个的绘制左边缘正好落在容器左边，最后一个的绘制右边缘正好接上加号。
        var running = 0.0;

        for (var i = 0; i < count; i++)
        {
            var shift = running + extra[i] / 2;
            running += extra[i];

            var transform = GetTransform(_vm.Items[i], (FrameworkElement)DockItems.ItemContainerGenerator.ContainerFromIndex(i));
            if (transform is null) continue;
            transform.Value.Scale.ScaleX = scales[i];
            transform.Value.Scale.ScaleY = scales[i];

            if (vertical)
            {
                transform.Value.Shift.Y = shift;
                transform.Value.Shift.X = 0;
            }
            else
            {
                transform.Value.Shift.X = shift;
                transform.Value.Shift.Y = 0;
            }
        }
    }

    private void OnSurfaceMouseLeave(object sender, MouseEventArgs e)
    {
        ResetMagnification();
        FadePointerGlow(0.22);
    }

    /// <summary>
    /// 镜面反光跟着鼠标走：径向渐变中心横坐标跟着指针，光就像在玻璃表面滑动。
    /// 这是液态玻璃最像"玻璃"的那一下——静态高光只能做出磨砂感。
    /// </summary>
    private void UpdatePointerGlow(MouseEventArgs e)
    {
        var width = GlassRoot.ActualWidth;
        if (width <= 0) return;

        var relativeX = Math.Clamp(e.GetPosition(GlassRoot).X / width, 0, 1);
        var center = new Point(relativeX, 0.62);

        PointerGlowBrush.Center = center;
        PointerGlowBrush.GradientOrigin = center;

        if (PointerGlow.Opacity < 1) FadePointerGlow(1.0);
    }

    private void FadePointerGlow(double target)
    {
        PointerGlow.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void ResetMagnification()
    {
        foreach (var (scale, shift) in _transforms.Values)
        {
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            shift.X = 0;
            shift.Y = 0;
        }

        // 玻璃条收回原始尺寸（横竖都清，切换停靠边时不会残留）
        DockItems.Width = double.NaN;
        DockItems.Height = double.NaN;
        _lastTotalExtra = 0;
    }

    /// <summary>首次用到某个图标时，从模板里把 IconRoot 找出来挂上变换，并缓存复用。</summary>
    private (ScaleTransform Scale, TranslateTransform Shift)? GetTransform(DockItem item, FrameworkElement container)
    {
        container.ApplyTemplate();
        if (container is not ContentPresenter presenter) return null;
        if (presenter.ContentTemplate?.FindName("IconRoot", presenter) is not Grid root) return null;

        // 缓存命中还不够，得确认这对变换确实挂在这个容器的根元素上：
        // 容器被重建时旧引用会指向已经离开视觉树的元素，继续用就完全不放大。
        if (_transforms.TryGetValue(item, out var cached)
            && root.RenderTransform is TransformGroup attached
            && attached.Children.Contains(cached.Scale))
        {
            return cached;
        }

        var scale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform(0, 0);

        // 顺序很重要：先以底边中点为锚放大，再整体横移，位移才不会被缩放放大
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(shift);
        root.RenderTransform = group;

        var result = (scale, shift);
        _transforms[item] = result;
        return result;
    }

    // ---------------- 玻璃折射 ----------------

    /// <summary>
    /// 折射层：定时抓 Dock 背后的画面，边缘按法线方向弯折后贴回玻璃上。
    ///
    /// 前提是把自己从屏幕捕获里排除掉（WDA_EXCLUDEFROMCAPTURE），否则抓到的是 Dock 自己，
    /// 折射层会一遍遍拍自己，画面越来越亮。系统不支持（Win10 2004 以前）就直接不启用。
    /// </summary>
    private void StartRefraction()
    {
        // 【已停用】抓屏折射的前提是把自己从屏幕捕获里排除（WDA_EXCLUDEFROMCAPTURE），
        // 代价是 Dock 在任何截图 / 录屏 / 远程桌面里都拍不到 —— 用户反馈这是硬伤，
        // 而且每次鼠标移动引起的尺寸变化都要重算折射几何，画面一直在抖。
        //
        // 现在玻璃效果全部由 XAML 里的多层高光、顶部聚光、色散轮廓实现，
        // RefractionLayer 始终隐藏。换来三件事：能截图、不闪、少一个 40~50fps 的抓屏线程。
        // GlassRefractor / ScreenSampler 留着没删 —— 将来若要改成「可选的折射」，
        // 在这里接回来即可（记得同时处理捕获排除）。
        RefractionLayer.Visibility = Visibility.Collapsed;
        StopRefraction();
    }

    /// <summary>
    /// 停掉折射线程，并把「从捕获中排除」的标记撤掉 —— Dock 要能被正常截图。
    /// 现在折射本身已经不启用了（见 <see cref="StartRefraction"/>），这里主要起兜底作用：
    /// 老版本留下的排除标记、以及切换样式时的残留，都在这里清干净。
    /// </summary>
    private void StopRefraction()
    {
        _glassRunning = false;

        // 折射线程现在不会启动了，但留着这段等待：万一将来把折射接回来，
        // 这里能保证退出的那一帧不会读到已经释放的缓冲。
        if (_glassWorker is { IsAlive: true })
        {
            _glassWorker.Join(TimeSpan.FromMilliseconds(200));
        }

        _glassWorker = null;
        Volatile.Write(ref _glassGeometry, null);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowDisplayAffinity(hwnd, 0 /* WDA_NONE */);
        }
    }

    private static void LogGlass(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "glass.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响功能
        }
    }

    /// <summary>
    /// 把玻璃的屏幕位置、尺寸、圆角算成快照，交给后台线程用。
    /// 只能在 UI 线程调用（PointToScreen 要求）。
    /// </summary>
    private void UpdateGlassGeometry()
    {
        if (!_glassRunning) return;

        var glassWidth = GlassRoot.ActualWidth;
        var glassHeight = GlassRoot.ActualHeight;
        if (glassWidth < 8 || glassHeight < 8) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = GlassRoot.PointToScreen(new Point(0, 0));

        var width = (int)Math.Round(glassWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(glassHeight * dpi.DpiScaleY);
        if (width < 8 || height < 8) return;

        // 半分辨率处理：像素量降到 1/4，最后靠 WPF 双线性放大，
        // 反正玻璃本来就是糊的，肉眼看不出差别，帧率却能翻倍。
        const int step = 2;
        var outputWidth = Math.Max(1, width / step);
        var outputHeight = Math.Max(1, height / step);

        // 边缘要往外采样，所以抓屏范围比 Dock 大一圈
        var padding = (int)Math.Round(18 * dpi.DpiScaleX);

        Volatile.Write(ref _glassGeometry, new GlassGeometry(
            (int)origin.X, (int)origin.Y, width, height,
            outputWidth, outputHeight,
            step, padding,
            _vm.Settings.CornerRadius * dpi.DpiScaleX,
            15 * dpi.DpiScaleX,
            // 液态透明玻璃要的是「轻微折射」，形变收一半
            _vm.Settings.IsLiquidClear ? 0.62 : 1.15,
            dpi.DpiScaleX));
    }

    /// <summary>后台线程：抓屏 → 降采样 → 模糊 → 边缘形变，算完把帧丢回 UI 线程。</summary>
    private void GlassWorkerLoop()
    {
        while (_glassRunning)
        {
            var geometry = Volatile.Read(ref _glassGeometry);

            if (geometry is null)
            {
                Thread.Sleep(30);
                continue;
            }

            try
            {
                var sourceWidth = geometry.Width + geometry.Padding * 2;
                var sourceHeight = geometry.Height + geometry.Padding * 2;
                var required = sourceWidth * sourceHeight * 4;

                if (_captureBuffer is null || _captureBuffer.Length != required)
                {
                    _captureBuffer = new byte[required];
                }

                var captured = ScreenSampler.Capture(
                    geometry.X - geometry.Padding,
                    geometry.Y - geometry.Padding,
                    sourceWidth, sourceHeight,
                    _captureBuffer);

                if (captured)
                {
                    var frame = _refractor.Render(
                        _captureBuffer, sourceWidth, sourceHeight,
                        geometry.Step,
                        geometry.OutputWidth, geometry.OutputHeight,
                        geometry.Padding,
                        geometry.Radius, geometry.Edge, geometry.Push);

                    if (frame is not null)
                    {
                        Dispatcher.BeginInvoke(() => Present(frame), DispatcherPriority.Render);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AquaDesk] 折射失败：{ex.Message}");
            }

            // 约 60fps 上限；实际会被抓屏 + 形变的耗时压到 40~50fps
            Thread.Sleep(16);
        }
    }

    /// <summary>UI 线程：把后台算好的帧贴到 WriteableBitmap 上。开销只有一次内存拷贝。</summary>
    private void Present(byte[] frame)
    {
        var geometry = Volatile.Read(ref _glassGeometry);
        if (geometry is null) return;

        var expected = geometry.OutputWidth * geometry.OutputHeight * 4;
        if (frame.Length != expected) return;

        if (_refractionBitmap is null
            || _refractionWidth != geometry.OutputWidth
            || _refractionHeight != geometry.OutputHeight)
        {
            _refractionWidth = geometry.OutputWidth;
            _refractionHeight = geometry.OutputHeight;

            // dpi 按降采样倍数缩小，位图的 DIP 尺寸才和玻璃条对齐
            var dpi = 96.0 * geometry.DpiScale / geometry.Step;
            _refractionBitmap = new WriteableBitmap(
                geometry.OutputWidth, geometry.OutputHeight, dpi, dpi, PixelFormats.Pbgra32, null);
            RefractionBrush.ImageSource = _refractionBitmap;
        }

        _refractionBitmap.WritePixels(
            new Int32Rect(0, 0, geometry.OutputWidth, geometry.OutputHeight),
            frame, geometry.OutputWidth * 4, 0);
    }

    // ---------------- 拖放添加 ----------------

    /// <summary>能拖进来的类型。跟「添加应用…」的过滤器保持一致。</summary>
    private static readonly string[] DropExtensions = { ".exe", ".lnk" };

    private static bool TryGetDroppedFiles(DragEventArgs e, out string[] files)
    {
        files = Array.Empty<string>();

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return false;

        files = dropped.Where(IsSupported).ToArray();
        return files.Length > 0;
    }

    private static bool IsSupported(string path) =>
        DropExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedFiles(e, out _))
        {
            e.Effects = DragDropEffects.None;
            HideDropIndicator();
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(DockItems);

        // 落在文件夹图标上：松手是「放进文件夹」，不插进 Dock
        if (FolderAt(position) is not null)
        {
            e.Effects = DragDropEffects.Copy;
            HideDropIndicator();
            e.Handled = true;
            return;
        }

        // 落在启动台图标上：松手是「加进启动台」，不是插入 Dock，别画插入条
        if (IsOverLaunchpadIcon(position))
        {
            e.Effects = DragDropEffects.Copy;
            HideDropIndicator();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        UpdateDropIndicator(position);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => HideDropIndicator();

    private void OnDrop(object sender, DragEventArgs e)
    {
        HideDropIndicator();

        if (!TryGetDroppedFiles(e, out var files)) return;

        var position = e.GetPosition(DockItems);

        // 落在文件夹图标上 → 全塞进这个文件夹
        if (FolderAt(position) is { } target)
        {
            foreach (var file in files)
            {
                _vm.AddToFolder(target, file);
            }

            _vm.RefreshStructure();
            UpdateEmptyState();
            UpdateWindowWidth();
            e.Handled = true;
            return;
        }

        // 落在启动台图标上 → 加进启动台；落在别处 → 加进 Dock
        if (IsOverLaunchpadIcon(position))
        {
            GetOrCreateLaunchpad().AddFiles(files);
            e.Handled = true;
            return;
        }

        // GetDropIndex 给的是 Items 索引，_vm.Insert 要的是 _configs 索引
        var index = GetDropIndex(position);

        foreach (var file in files)
        {
            // 插一个往后挪一位，多个文件按顺序落位
            if (_vm.Insert(index, file)) index++;
        }

        UpdateEmptyState();
        UpdateWindowWidth();
        e.Handled = true;
    }

    /// <summary>落点落在哪个图标的左半边，就插在它前面。</summary>
    private int GetDropIndex(Point positionInItems)
    {
        // 只在应用区里找落点：两端的系统项不参与
        var appEnd = _vm.AppEnd;

        for (var i = 0; i < appEnd; i++)
        {
            if (DockItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            var topLeft = container.TranslatePoint(new Point(0, 0), DockItems);
            var center = _vm.Settings.IsVertical
                ? topLeft.Y + container.ActualHeight / 2
                : topLeft.X + container.ActualWidth / 2;

            var pointer = _vm.Settings.IsVertical ? positionInItems.Y : positionInItems.X;
            if (pointer < center) return i;
        }

        return appEnd;
    }

    /// <summary>落点是不是压在了启动台图标上。</summary>
    private bool IsOverLaunchpadIcon(Point positionInItems)
    {
        // 启动台不再是固定的第 2 个（品牌和 Windows 图标都能关掉），得现找
        var launchpadIndex = -1;

        for (var i = 0; i < _vm.Items.Count; i++)
        {
            if (_vm.Items[i].IsLaunchpad)
            {
                launchpadIndex = i;
                break;
            }
        }

        if (launchpadIndex < 0) return false;
        if (DockItems.ItemContainerGenerator.ContainerFromIndex(launchpadIndex) is not FrameworkElement container)
            return false;

        var origin = container.TranslatePoint(new Point(0, 0), DockItems);
        return new Rect(origin, container.RenderSize).Contains(positionInItems);
    }

    /// <summary>落点压在哪个文件夹图标上；没有就返回 null。</summary>
    private DockItem? FolderAt(Point positionInItems)
    {
        for (var i = 0; i < _vm.Items.Count; i++)
        {
            if (!_vm.Items[i].IsFolder) continue;

            if (DockItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;

            var origin = container.TranslatePoint(new Point(0, 0), DockItems);
            if (new Rect(origin, container.RenderSize).Contains(positionInItems)) return _vm.Items[i];
        }

        return null;
    }

    private void UpdateDropIndicator(Point positionInItems)
    {
        if (_vm.Items.Count == 0)
        {
            HideDropIndicator();
            return;
        }

        var index = GetDropIndex(positionInItems);
        var vertical = _vm.Settings.IsVertical;
        var icon = _vm.Settings.ScaledIconSize;

        double along;

        if (index < _vm.Items.Count
            && DockItems.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement next)
        {
            along = vertical
                ? next.TranslatePoint(new Point(0, 0), GlassRoot).Y
                : next.TranslatePoint(new Point(0, 0), GlassRoot).X;
        }
        else if (DockItems.ItemContainerGenerator.ContainerFromIndex(_vm.Items.Count - 1) is FrameworkElement last)
        {
            along = vertical
                ? last.TranslatePoint(new Point(0, last.ActualHeight), GlassRoot).Y
                : last.TranslatePoint(new Point(last.ActualWidth, 0), GlassRoot).X;
        }
        else
        {
            HideDropIndicator();
            return;
        }

        if (vertical)
        {
            DropIndicator.Width = icon;
            DropIndicator.Height = 3;
            Canvas.SetLeft(DropIndicator, (GlassRoot.ActualWidth - icon) / 2);
            Canvas.SetTop(DropIndicator, along - 1.5);
        }
        else
        {
            DropIndicator.Width = 3;
            DropIndicator.Height = icon;
            Canvas.SetLeft(DropIndicator, along - 1.5);
            Canvas.SetTop(DropIndicator, (GlassRoot.ActualHeight - icon) / 2);
        }

        DropIndicator.Visibility = Visibility.Visible;
    }

    private void HideDropIndicator() => DropIndicator.Visibility = Visibility.Collapsed;

    // ---------------- 拖动排序 / 拖出移除 ----------------

    private DockItem? _dragItem;
    private Point _dragOrigin;
    private bool _dragging;
    private bool _removePending;
    private DateTime _dragEndedAt = DateTime.MinValue;

    /// <summary>双击去抖：记住上一次点的是哪个图标、什么时候。</summary>
    private DockItem? _lastClickedItem;
    private DateTime _lastClickedAt = DateTime.MinValue;
    private DispatcherTimer? _removeTimer;

    /// <summary>按住图标先记下来，等移动超过阈值才真的开始拖——不然单击也会被当成拖。</summary>
    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DockItem item }) return;

        // 品牌和启动台固定在最左边，不参与拖动排序
        // 分隔线和废纸篓钉死在尾部，其余（含系统图标）都能拖
        if (!item.CanDrag) return;

        _dragItem = item;
        _dragOrigin = e.GetPosition(this);

        // 注意：这里不能 CaptureMouse。一旦捕获，鼠标事件的目标就变成捕获元素，
        // 图标上的 MouseLeftButtonUp（OnItemClick）再也收不到，单击会失效。
        // 捕获放到 BeginDrag 里做——那时才真的开始拖。
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null) return;

        if (!_dragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragItem = null;
                return;
            }

            var current = e.GetPosition(this);
            if (Math.Abs(current.X - _dragOrigin.X) < 8 && Math.Abs(current.Y - _dragOrigin.Y) < 8)
                return;

            BeginDrag();
        }

        UpdateDrag(e.GetPosition(this));
    }

    // 拖动浮层：独立窗口，能移到屏幕外，不会被 Dock 窗口或 Popup 的边界规则限制住
    private DragOverlayWindow? _dragOverlay;

    private void BeginDrag()
    {
        if (_dragItem is null) return;

        _dragging = true;
        ResetMagnification();
        CaptureMouse();

        try
        {
            _dragOverlay ??= new DragOverlayWindow();
            _dragOverlay.ShowItem(_dragItem.Icon, _vm.Settings.ScaledIconSize);

            var anchor = ToScreenDip(Mouse.GetPosition(this));

            if (!_dragOverlay.IsVisible)
            {
                // 先按 DIP 摆个大概位置再显示，避免窗口在屏幕中央闪一下
                _dragOverlay.PlaceBeforeShow(anchor.X, anchor.Y);
                _dragOverlay.Show();
            }

            _dragOverlay.MoveTo(anchor.X, anchor.Y);
        }
        catch (Exception ex)
        {
            // 浮层挂了不该影响拖动本身（排序和移除照样能用），只记一条调试输出
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 拖动浮层失败：{ex.Message}");
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aquadesk-drag.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} 浮层失败: {ex}{Environment.NewLine}");
            }
            catch { }
        }

        // 原位置淡掉，表示这个图标正在被搬
        if (FindContainer(_dragItem) is { } container) container.Opacity = 0.3;
    }

    private void UpdateDrag(Point position)
    {
        if (!_dragging || _dragItem is null) return;

        var anchor = ToScreenDip(position);
        _dragOverlay?.MoveTo(anchor.X, anchor.Y);

        if (IsOverGlass(position))
        {
            CancelRemoveTimer();
            UpdateDropIndicator(this.TranslatePoint(position, DockItems));
        }
        else
        {
            HideDropIndicator();
            StartRemoveTimer();
        }
    }

    /// <summary>窗口内坐标 → 屏幕 DIP 坐标（浮层的 Left/Top 用这个单位）。</summary>
    private Point ToScreenDip(Point windowPosition)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = PointToScreen(windowPosition);
        return new Point(screen.X / dpi.DpiScaleX, screen.Y / dpi.DpiScaleY);
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem is null) return;

        var item = _dragItem;
        var wasDragging = _dragging;
        var remove = _removePending;

        EndDrag();

        if (!wasDragging) return;

        _dragEndedAt = DateTime.Now;

        if (remove)
        {
            // 系统图标拖出去 = 关掉它（设置页会同步变成「关闭」，随时能再打开）。
            // 不直接删配置，否则设置里还显示着「已开启」、Dock 上却没有，对不上。
            if (item.IsSystem)
            {
                TurnOffSystemIcon(item);
                return;
            }

            _transforms.Remove(item);
            _vm.Remove(item);
            UpdateEmptyState();
            UpdateWindowWidth();
            return;
        }

        var from = _vm.Items.IndexOf(item);
        if (from < 0) return;

        _vm.Move(from, GetDropIndex(e.GetPosition(DockItems)));
    }

    private void EndDrag()
    {
        CancelRemoveTimer();

        if (_dragItem is not null && FindContainer(_dragItem) is { } container)
        {
            container.Opacity = 1;
        }

        _dragItem = null;
        _dragging = false;
        _removePending = false;

        _dragOverlay?.Hide();
        HideDropIndicator();

        if (IsMouseCaptured) ReleaseMouseCapture();

        // 拖动过程中每个图标都被算过缩放和位移，松手不复位的话
        // 它们会带着那套偏移留在原地，看起来就是图标互相压在一起。
        ResetMagnification();
    }

    private FrameworkElement? FindContainer(DockItem item) =>
        DockItems.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;

    /// <summary>指针还在不在玻璃条范围内（留 6px 容差，避免边缘抖动）。</summary>
    private bool IsOverGlass(Point windowPosition)
    {
        var origin = GlassRoot.TranslatePoint(new Point(0, 0), this);
        const double tolerance = 6;

        return windowPosition.X >= origin.X - tolerance
            && windowPosition.X <= origin.X + GlassRoot.ActualWidth + tolerance
            && windowPosition.Y >= origin.Y - tolerance
            && windowPosition.Y <= origin.Y + GlassRoot.ActualHeight + tolerance;
    }

    /// <summary>在玻璃条外面停留 2 秒，才把「移除」提示亮出来。</summary>
    private void StartRemoveTimer()
    {
        if (_removeTimer is not null) return;

        _removeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _removeTimer.Tick += (_, _) =>
        {
            CancelRemoveTimer();
            _removePending = true;
            _dragOverlay?.ShowRemoveHint(_vm.Settings.ScaledIconSize);
        };
        _removeTimer.Start();
    }

    private void CancelRemoveTimer()
    {
        _removeTimer?.Stop();
        _removeTimer = null;

        if (_removePending)
        {
            _removePending = false;
            _dragOverlay?.HideRemoveHint();
        }
    }

    // ---------------- 右键菜单 ----------------

    private DispatcherTimer? _menuWatch;

    /// <summary>
    /// Dock 窗口是 WS_EX_NOACTIVATE，右键菜单弹出后不会因为「窗口失活」自动关闭，
    /// 点别的窗口时菜单会赖着不走。这里自己盯着：只要在菜单外面按下鼠标就关掉。
    /// </summary>
    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        UpdateWindowPreviewMenu(menu);

        _menuWatch?.Stop();
        _menuWatch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };

        _menuWatch.Tick += (_, _) =>
        {
            if (!menu.IsOpen)
            {
                _menuWatch?.Stop();
                return;
            }

            if (!IsAnyMouseButtonDown() || IsCursorInside(menu)) return;

            menu.IsOpen = false;
            _menuWatch?.Stop();
        };

        _menuWatch.Start();
    }

    private static bool IsAnyMouseButtonDown() =>
        (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0     // 左键
        || (NativeMethods.GetAsyncKeyState(0x02) & 0x8000) != 0; // 右键

    private static bool IsCursorInside(FrameworkElement element)
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return true;

        var topLeft = element.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(element);

        var width = element.ActualWidth * dpi.DpiScaleX;
        var height = element.ActualHeight * dpi.DpiScaleY;

        return cursor.X >= topLeft.X && cursor.X <= topLeft.X + width
            && cursor.Y >= topLeft.Y && cursor.Y <= topLeft.Y + height;
    }

    private void OnMenuCloseApp(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuItemData(sender) is not { } item) return;

        // 回收站没有独立进程，只能关窗口
        if (item.IsTrash)
        {
            WindowQuery.CloseTrash();
            _vm.RefreshStates();
            return;
        }

        item.Close();
        _vm.RefreshStates();
    }

    // ---------------- 增删 ----------------

    private void OnMenuAdd(object sender, RoutedEventArgs e) => AddFromDialog();

    /// <summary>新建一个空文件夹，插到应用区末尾。</summary>
    private void OnMenuNewFolder(object sender, RoutedEventArgs e)
    {
        _vm.AddFolder();

        UpdateEmptyState();
        UpdateWindowWidth();
        Dispatcher.BeginInvoke(PositionDock, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>往文件夹里放东西：程序、文档、图片都行，选完直接进文件夹。</summary>
    private void OnMenuAddToFolder(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuItemData(sender) is not { IsFolder: true } folder) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要放进文件夹的文件或程序",
            Filter = "所有文件 (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };

        if (dialog.ShowDialog(this) != true) return;

        foreach (var file in dialog.FileNames)
        {
            _vm.AddToFolder(folder, file);
        }

        _vm.RefreshStructure();
        UpdateEmptyState();
        UpdateWindowWidth();
    }

    private void AddFromDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要加入 Dock 的程序",
            Filter = "程序 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };

        if (dialog.ShowDialog(this) != true) return;

        foreach (var file in dialog.FileNames)
        {
            _vm.Add(file);
        }

        UpdateEmptyState();
        UpdateWindowWidth();
    }

    private void OnMenuRemove(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuItemData(sender) is not { } item) return;

        _transforms.Remove(item);
        _vm.Remove(item);
        UpdateEmptyState();
        UpdateWindowWidth();
    }

    private void OnMenuOpen(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuItemData(sender) is { } item) item.Launch();
    }

    /// <summary>
    /// ContextMenu 不在可视树里，MenuItem 的 DataContext 未必是 DockItem，
    /// 稳妥做法是从 PlacementTarget 上取。
    /// </summary>
    private static DockItem? ResolveMenuItemData(object sender)
    {
        if (sender is not MenuItem menuItem) return null;
        if (menuItem.DataContext is DockItem direct) return direct;

        if (menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement target }
            && target.DataContext is DockItem fromTarget)
        {
            return fromTarget;
        }

        return null;
    }

    // ---------------- 点击启动 ----------------

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        // 拖动结束时的松手也会触发这个事件，别把它当成"点击启动"
        if (_dragging) return;
        if ((DateTime.Now - _dragEndedAt).TotalMilliseconds < 250) return;

        if (sender is not FrameworkElement { DataContext: DockItem item }) return;

        // 双击去抖：第一下最小化、第二下又还原，合起来就像「点了没反应」。
        // 同一个图标 350ms 内的重复点击只认第一次。
        if (ReferenceEquals(_lastClickedItem, item)
            && (DateTime.Now - _lastClickedAt).TotalMilliseconds < 350)
        {
            return;
        }

        _lastClickedItem = item;
        _lastClickedAt = DateTime.Now;

        switch (item.Kind)
        {
            case DockItemKind.Brand:
                ShowSettings();
                break;

            case DockItemKind.Launchpad:
                OpenLaunchpad();
                break;

            case DockItemKind.Windows:
                ShowPowerMenu(sender as FrameworkElement);
                break;

            case DockItemKind.Computer:
                OpenComputer();
                break;

            case DockItemKind.Folder:
                OpenFolder(item);
                break;

            case DockItemKind.Window:
                // 窗口预览：已经在前台就最小化，最小化的就还原，其余切到前台
                WindowPreviewService.Toggle(item.WindowHandle);
                break;

            case DockItemKind.Trash:
                // 和其他应用一样：没开就打开，开着在前台就最小化，在后台就提到前台
                if (!WindowQuery.ToggleTrash()) OpenTrash();
                _vm.RefreshStates();
                break;

            case DockItemKind.Separator:
                return;

            default:
                // 没开 → 启动；开着且在前台 → 最小化；开着但在后台 → 提到前台
                Services.AppWindowController.Toggle(item);

                // 窗口状态刚变过，立刻刷一次指示条，不然要等下一个 800ms 周期
                _vm.RefreshStates();
                break;
        }

        ResetMagnification();
    }

    // ---------------- 菜单 ----------------

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsLoaded: true } existing)
        {
            // 纯切换，不判断「是不是前台」。
            //
            // 试过用 IsActive / GetForegroundWindow 判断，都不可靠：Dock 是 NOACTIVATE 窗口，
            // 从这里打开/点击时系统给的前台状态经常对不上，结果永远走不到「收起」那条分支。
            // 与其猜，不如做成确定行为：没最小化就收起，已最小化就还原。
            if (existing.WindowState == WindowState.Minimized)
            {
                App.Log($"设置窗口：还原（点图标），原状态={existing.WindowState}", null);
                BringToFront(existing);
                return;
            }

            App.Log($"设置窗口：收起（点图标），原状态={existing.WindowState}", null);
            existing.WindowState = WindowState.Minimized;
            return;
        }

        _settingsWindow = new SettingsWindow(_vm.Settings) { Owner = null };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;

            // 设置页关掉，品牌图标下方的指示条也跟着灭
            _vm.IsSettingsOpen = false;
            _vm.RefreshStates();
        };

        // 打开设置 → 品牌图标亮起运行指示条
        _vm.IsSettingsOpen = true;
        _vm.RefreshStates();

        _settingsWindow.Show();

        // 从 NOACTIVATE 的 Dock 里 Show 出来的窗口常常拿不到前台权限：
        // 看着在最上面，实际前台还是别的窗口。补一次，这样第一次点图标就能直接收起。
        BringToFront(_settingsWindow);
    }

    /// <summary>
    /// 把一个已经存在的窗口叫到前面。三件事缺一不可：
    /// 1. **最小化必须先 ShowWindow(SW_RESTORE)** —— 对最小化的窗口调 Activate 只会闪一下任务栏，
    ///    窗口根本不会回来（WPF 的 WindowState 改回 Normal 有时候也不顶用）；
    /// 2. Topmost 切一下，把它顶到 z 序最上层；
    /// 3. Dock 是 NOACTIVATE 窗口，点它的时候本进程不在前台，系统会用前台锁定挡掉
    ///    SetForegroundWindow，所以失败时退回更老的 SwitchToThisWindow。
    /// </summary>
    private static void BringToFront(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.Activate();
            return;
        }

        // WPF 的状态和 Win32 的状态各管各的，两边都还原一遍最稳
        if (window.WindowState == WindowState.Minimized || NativeMethods.IsIconic(handle))
        {
            window.WindowState = WindowState.Normal;
            NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
        }

        window.Show();
        window.Topmost = true;
        window.Activate();

        if (!NativeMethods.SetForegroundWindow(handle))
        {
            NativeMethods.SwitchToThisWindow(handle, true);
        }

        window.Topmost = false;
    }

    private void OnExitApp(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ---------------- Windows 电源菜单 ----------------

    /// <summary>
    /// 左键点 Windows 图标弹出电源菜单。菜单挂在图标正上方，和 macOS 的苹果菜单一个方向。
    /// PowerMenu 声明了 x:Shared="False"，每次取都是新实例，不会被上一个挂住。
    /// </summary>
    private void ShowPowerMenu(FrameworkElement? anchor)
    {
        if (anchor is null) return;
        if (TryFindResource("PowerMenu") is not ContextMenu menu) return;

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Top;
        menu.VerticalOffset = -6;
        menu.IsOpen = true;
    }

    private void OnPowerLock(object sender, RoutedEventArgs e) => PowerService.Lock();

    private void OnPowerSleep(object sender, RoutedEventArgs e) => PowerService.Sleep();

    private void OnPowerShutdown(object sender, RoutedEventArgs e) => PowerService.Shutdown();

    private void OnPowerRestart(object sender, RoutedEventArgs e) => PowerService.Restart();

    // ---------------- 废纸篓 ----------------

    private void OnOpenTrash(object sender, RoutedEventArgs e) => OpenTrash();

    private void OnEmptyTrash(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, 0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 清空回收站失败：{ex.Message}");
        }
    }

    private static void OpenTrash()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shell:RecycleBinFolder")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 打开回收站失败：{ex.Message}");
        }
    }

    // ---------------- 我的电脑 ----------------

    private void OnOpenComputer(object sender, RoutedEventArgs e) => OpenComputer();

    private void OpenComputer()
    {
        ResetMagnification();

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shell:MyComputerFolder")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 打开我的电脑失败：{ex.Message}");
        }
    }

    // ---------------- 文件夹 ----------------

    private DockFolderWindow? _folderWindow;

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuItemData(sender) is { IsFolder: true } folder) OpenFolder(folder);
    }

    private void OnRenameFolder(object sender, RoutedEventArgs e)
    {
        if (ResolveMenuItemData(sender) is not { IsFolder: true } folder) return;

        var name = InputDialog.Ask("重命名文件夹", folder.Name);
        if (name is null) return;

        if (!_vm.RenameFolder(folder, name)) return;

        _vm.RefreshStructure();
        UpdateEmptyState();
        UpdateWindowWidth();
        Dispatcher.BeginInvoke(PositionDock, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 文件夹用独立窗口打开，界面照搬启动台（同一个暗色底 + 7×4 网格）。
    /// 和启动台一样，打开期间把 Topmost 让出来，避免两个置顶窗口互相压。
    /// </summary>
    private void OpenFolder(DockItem folder)
    {
        ResetMagnification();

        if (_folderWindow is { IsLoaded: true }) _folderWindow.Close();

        Topmost = false;

        _folderWindow = new DockFolderWindow(_vm, folder);
        _folderWindow.Closed += (_, _) =>
        {
            _folderWindow = null;
            Topmost = true;

            // 窗口里可能增删过内容，回来后把 Dock 上的缩略图和宽度重算一遍
            _vm.RefreshStructure();
            UpdateEmptyState();
            UpdateWindowWidth();
            Dispatcher.BeginInvoke(PositionDock, System.Windows.Threading.DispatcherPriority.Loaded);
        };

        _folderWindow.Show();
    }

    // ---------------- 启动台 ----------------

    private LaunchpadWindow? _launchpad;

    private void OnMenuLaunchpad(object sender, RoutedEventArgs e)
    {
        OpenLaunchpad();
    }


    private void OpenLaunchpad()
    {
        ResetMagnification();

        var pad = GetOrCreateLaunchpad();
        pad.Activate();
    }

    /// <summary>
    /// 启动台是模态的：打开期间把 Dock 的 Topmost 让出来，
    /// 否则两个 Topmost 窗口会互相压，点桌面时启动台可能被挤下去。
    /// </summary>
    private LaunchpadWindow GetOrCreateLaunchpad()
    {
        if (_launchpad is { IsLoaded: true }) return _launchpad;

        Topmost = false;

        _launchpad = new LaunchpadWindow();
        _launchpad.Closed += (_, _) =>
        {
            _launchpad = null;
            Topmost = true;
        };

        _launchpad.Show();
        return _launchpad;
    }

    // ---------------- 给托盘用的公开入口 ----------------

    /// <summary>托盘菜单 / 双击托盘：显示或隐藏 Dock。</summary>
    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
            StopRefraction();
        }
        else
        {
            Show();
            PositionDock();
            ApplyStyle();
        }
    }

    public void ShowSettings() => OnOpenSettings(this, new RoutedEventArgs());

    /// <summary>工作区变了（任务栏隐藏 / 菜单栏占位）之后，让 Dock 重新贴一次底。</summary>
    public void RefreshLayout()
    {
        UpdateWindowWidth();
        PositionDock();
    }

    /// <summary>等 Shell 把工作区改完再贴底。一次性定时器，不占常驻开销。</summary>
    private void ScheduleRelayout()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshLayout();
        };

        timer.Start();
    }
}
