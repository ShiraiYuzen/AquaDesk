using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using AquaDesk.Services;

namespace AquaDesk.Models;

/// <summary>
/// 界面风格。
/// Liquid = 液态玻璃 —— **已下架**，只为读得懂老配置而保留枚举值，读到就归到 LiquidClear；
/// Classic = 最初 0.01 的样子；
/// LiquidClear = 液态透明玻璃：中间透、色散彩虹边、轻微折射（默认）。
/// </summary>
public enum DockStyle
{
    Liquid = 0,
    Classic = 1,
    LiquidClear = 2
}

/// <summary>Dock 停靠在屏幕的哪条边。</summary>
public enum DockEdge
{
    Bottom = 0,
    Top = 1,
    Left = 2,
    Right = 3
}

/// <summary>
/// 什么情况下让 Dock 停下来、把资源还给系统。
/// 「暂停」= 停掉折射抓屏和状态轮询 + 丢掉缓冲 + 回收工作集，Dock 本身照常显示。
/// </summary>
public enum DockPauseMode
{
    /// <summary>不暂停，一直跑。</summary>
    Never = 0,

    /// <summary>其他应用成为前台时暂停（鼠标移到 Dock 上立刻恢复）。</summary>
    Unfocused = 1,

    /// <summary>检测到无边框全屏（游戏那种 WS_POPUP 全屏）时暂停。</summary>
    BorderlessFullscreen = 2,

    /// <summary>检测到任何全屏应用（含浏览器 F11）时暂停。</summary>
    Fullscreen = 3
}

/// <summary>
/// Dock 和两个胶囊的窗口层级（它们平时是 Topmost 浮层，会盖在别的窗口上面）。
/// 见 <c>Services/OverlayZOrder</c>。
/// </summary>
public enum DockTopMode
{
    /// <summary>前台窗口最大化 / 全屏时自动沉到最底，还原后抬回最前（默认）。</summary>
    SinkWhenCovered = 0,

    /// <summary>始终显示在最上层，只有全屏应用（F11 / 无边框游戏）时才让开。</summary>
    AlwaysTop = 1,

    /// <summary>始终显示在最下层：任何窗口都能盖住它。</summary>
    AlwaysBottom = 2
}

/// <summary>
/// 用户可调的全部参数。直接双向绑定到设置页，改完立刻生效，不需要重启。
/// 所有 setter 都做区间收敛，避免手改 JSON 改出负数把界面搞崩。
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private DockStyle _style = DockStyle.LiquidClear;
    private bool _allowStyleSwitch;
    private double _glassOpacity = 0.26;
    private double _iconSize = 52;

    /// <summary>
    /// 图标太多时的自动收缩系数，由 DockWindow 按屏幕可用跨度现算现设。
    /// 不持久化：换显示器 / 改分辨率 / 删掉几个图标都应该重新算，不该被上次的值绑死。
    /// </summary>
    private double _autoShrinkScale = 1;
    private double _iconGap = 6;
    private double _cornerRadius = 26;
    private double _magnifyMaxScale = 1.55;
    private double _bottomMargin = 10;
    private bool _autoStart;
    private bool _hideTaskbar;
    private bool _hideDesktopIcons;
    // 0.4.9 起这两个开关的意思变了：管的是「状态胶囊上显不显示」，
    // Dock 上这两颗已经彻底不摆（见 DockViewModel.IsSystemEnabled）。
    // 所以默认是 true —— 它们是胶囊上唯一能进设置 / 弹电源的入口。

    private bool _showBrandIcon = true;
    private bool _showWindowsIcon = true;
    private bool _showLaunchpadIcon = true;
    private bool _showTrashIcon = true;
    private bool _showBackgroundAppsButton = true;
    private bool _showBatteryPercent;
    private bool _showComputerIcon = true;
    private bool _showWindowPreviews = true;

    /// <summary>预览只显示应用图标、不抓窗口内容（隐私模式）。</summary>
    private bool _previewIconOnly;
    private DockTopMode _topMode = DockTopMode.SinkWhenCovered;
    private DockEdge _edge = DockEdge.Bottom;
    private double _uiScale = DefaultUiScale();
    private DockPauseMode _pauseMode = DockPauseMode.Never;

    // ---------------- 灵动岛（顶部时间胶囊） ----------------

    /// <summary>是否显示灵动岛（顶部时间胶囊）。</summary>
    private bool _showIsland = true;

    /// <summary>灵动岛宽度（DIP），只作用于时间形态；播放形态按内容动态拉长。</summary>
    private double _islandWidth = 88;

    /// <summary>灵动岛高度（DIP），只作用于时间形态。</summary>
    private double _islandHeight = 30;

    /// <summary>灵动岛播放形态宽度（DIP），在自动拉长基准（256）上可调。</summary>
    private double _islandPlayerWidth = 256;

    /// <summary>灵动岛播放形态高度（DIP），在自动拉长基准（34）上可调。</summary>
    private double _islandPlayerHeight = 34;

    /// <summary>灵动岛 X（相对工作区左上角，DIP）。-1 表示自动水平居中。</summary>
    private double _islandX = -1;

    /// <summary>灵动岛 Y（相对工作区左上角，DIP）。-1 表示自动贴顶。</summary>
    private double _islandY = -1;

    /// <summary>true = 固定（不可拖动），false = 可自由拖动。</summary>
    private bool _islandFixed = true;

    // ---------------- 液态玻璃 ----------------

    /// <summary>
    /// 配置结构版本，用来做一次性迁移。
    /// 0.3.5：老配置可能停在「液态透明玻璃」，迁移时统一回到默认的液态玻璃。
    /// </summary>
    public int SettingsVersion { get; set; }

    /// <summary>界面风格，切换后立即生效。</summary>
    public DockStyle Style
    {
        get => _style;
        set
        {
            if (!Set(ref _style, value)) return;
            OnPropertyChanged(nameof(IsLiquid));
            OnPropertyChanged(nameof(IsClassic));
            OnPropertyChanged(nameof(IsLiquidClear));
            OnPropertyChanged(nameof(ShowsTint));
            OnPropertyChanged(nameof(StyleIndex));
            OnPropertyChanged(nameof(TintOpacity));
        }
    }

    /// <summary>
    /// 允许切换外观。默认关着 —— 关着时界面统一用液态透明玻璃，设置里也不显示风格选择，
    /// 免得误点到别的样式后不知道怎么切回来。
    /// </summary>
    public bool AllowStyleSwitch
    {
        get => _allowStyleSwitch;
        set
        {
            if (!Set(ref _allowStyleSwitch, value)) return;

            // 关掉开关就回到统一风格，避免停在某个被隐藏的样式上
            if (!value && _style != DockStyle.LiquidClear) Style = DockStyle.LiquidClear;
        }
    }

    /// <summary>给 ComboBox 用（SelectedIndex 双向绑定）。</summary>
    [JsonIgnore]
    public int StyleIndex
    {
        get => (int)_style;
        set => Style = value switch
        {
            (int)DockStyle.Classic => DockStyle.Classic,
            // 「液态玻璃」这个外观已经下架，老配置读到 0（Liquid）时落到液态透明玻璃
            _ => DockStyle.LiquidClear
        };
    }

    [JsonIgnore] public bool IsLiquid => _style == DockStyle.Liquid;

    /// <summary>中间透明、边缘渐变发光。</summary>
    [JsonIgnore] public bool IsLiquidClear => _style == DockStyle.LiquidClear;

    /// <summary>要不要画那层白色底色（边缘光样式不画，中间是透的）。</summary>
    [JsonIgnore] public bool ShowsTint => _style != DockStyle.LiquidClear;

    [JsonIgnore] public bool IsClassic => _style == DockStyle.Classic;

    /// <summary>底板不透明度。0 = 完全透明只剩高光，1 = 实心白。</summary>
    public double GlassOpacity
    {
        get => _glassOpacity;
        set
        {
            if (!Set(ref _glassOpacity, Clamp(value, 0, 1.0))) return;
            OnPropertyChanged(nameof(TintOpacity));
            OnPropertyChanged(nameof(LiquidBaseOpacity));
        }
    }

    /// <summary>
    /// 底色实际用的不透明度。
    ///
    /// 抓屏折射停用之后，液态玻璃和经典都只剩「底色 + 高光」这一层，
    /// 唯一的区别就落在底色的厚薄上：经典要更实（像四层玻璃），液态要更透。
    /// 直接拿 <see cref="GlassOpacity"/> 乘一个系数，滑块调起来两边都跟着变。
    /// </summary>
    [JsonIgnore]
    public double TintOpacity =>
        Math.Min(1, _glassOpacity * (_style == DockStyle.Classic ? 2.1 : 1.0));

    /// <summary>
    /// 液态透明玻璃那层自绘底色的实际不透明度。
    ///
    /// 默认外观（LiquidClear）不画 TintOpacity 那层（ShowsTint=false），
    /// 而是用自己的渐变底色，且这层 alpha 原来是写死的 —— 所以「底板不透明度」
    /// 滑块在默认外观下完全无效。这里把 GlassOpacity 映射到这一层：
    /// 以 0.26（默认值）为锚点返回 0.4，配合把渐变 alpha 放大 2.5 倍，
    /// 等效于原来的观感；往左更透、往右更实，整段滑块都有反馈。
    /// </summary>
    [JsonIgnore]
    public double LiquidBaseOpacity
    {
        get
        {
            var g = _glassOpacity;
            // 0 → 0（完全透明，只剩高光）；0.26（默认）→ 0.4（原观感）；0.95 及以上 → 1.0（最实）
            var o = g <= 0.26
                ? g / 0.26 * 0.4
                : 0.4 + (g - 0.26) * 0.87;
            return Clamp(o, 0.0, 1.0);
        }
    }

    /// <summary>Dock 底板圆角半径。</summary>
    public double CornerRadius
    {
        get => _cornerRadius;
        set => Set(ref _cornerRadius, Clamp(value, 10, 44));
    }

    // ---------------- Dock 尺寸 ----------------

    /// <summary>图标磁贴边长。</summary>
    public double IconSize
    {
        get => _iconSize;
        set
        {
            if (!Set(ref _iconSize, Clamp(value, 36, 88))) return;
            OnPropertyChanged(nameof(ScaledIconSize));
            RaiseDerived();
        }
    }

    /// <summary>图标之间的间距。</summary>
    public double IconGap
    {
        get => _iconGap;
        set
        {
            if (!Set(ref _iconGap, Clamp(value, 2, 20))) return;
            RaiseDerived();
        }
    }

    /// <summary>Dock 距所贴那条屏幕边的距离。</summary>
    public double BottomMargin
    {
        get => _bottomMargin;
        set
        {
            if (!Set(ref _bottomMargin, Clamp(value, 0, 200))) return;
            OnPropertyChanged(nameof(BottomMarginThickness));
            OnPropertyChanged(nameof(EdgeMarginThickness));
        }
    }

    // ---------------- 界面缩放 ----------------

    /// <summary>
    /// 界面缩放百分比（1.0 = 100%）。按屏幕物理分辨率给个默认值，
    /// 2K / 4K 屏上默认会放大一点，用户也可以在设置里手动调。
    /// </summary>
    public double UiScale
    {
        get => _uiScale;
        set
        {
            if (!Set(ref _uiScale, Clamp(value, 0.8, 1.5))) return;

            OnPropertyChanged(nameof(ScaledIconSize));
            OnPropertyChanged(nameof(UiScalePercent));
            RaiseDerived();
        }
    }

    /// <summary>给滑块显示用的百分比整数。</summary>
    [JsonIgnore]
    public double UiScalePercent
    {
        get => Math.Round(_uiScale * 100);
        set => UiScale = value / 100;
    }

    /// <summary>
    /// 实际参与布局的图标尺寸 = 用户设的尺寸 × 界面缩放。
    /// 布局里所有派生值都从它算，改一处全跟着变。
    /// </summary>
    [JsonIgnore]
    public double ScaledIconSize => _iconSize * _uiScale * _autoShrinkScale;

    /// <summary>
    /// 图标数量超出屏幕可用跨度时按比例整排缩小，保证最后一个图标也在屏幕内。
    /// 1 = 不缩；下限 0.35 是「再小就认不出图标了」的那条线，触底后宁可溢出。
    /// </summary>
    [JsonIgnore]
    public double AutoShrinkScale
    {
        get => _autoShrinkScale;
        set
        {
            var clamped = Clamp(value, 0.35, 1);

            // 抖动的来源：布局变化会再次触发重算，浮点误差必须挡掉，否则每帧都重排
            if (Math.Abs(clamped - _autoShrinkScale) < 0.002) return;

            _autoShrinkScale = clamped;
            OnPropertyChanged(nameof(AutoShrinkScale));
            OnPropertyChanged(nameof(ScaledIconSize));
            RaiseDerived();
        }
    }

    /// <summary>
    /// 按屏幕物理分辨率推一个默认缩放，同时照顾宽高比。
    ///
    /// 取宽、高两个比例里**较小**的那个：
    /// - 16:9  → 宽高比例相同，等价于按高度算
    /// - 16:10 → 高度比例更大，取宽度那个，避免 Dock 横向撑太满
    /// - 4:3   → 宽度比例明显更小，按宽度压下来，否则一排图标会占掉半屏
    ///
    /// 用物理像素而不是 DIP —— DIP 已经把系统缩放算进去了，再乘一次会重复放大。
    /// </summary>
    private static double DefaultUiScale()
    {
        try
        {
            var taskbar = Core.NativeMethods.FindWindowW("Shell_TrayWnd", null);
            var dpi = taskbar != IntPtr.Zero ? Core.NativeMethods.GetDpiForWindow(taskbar) : 96;

            var dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;
            var physicalWidth = SystemParameters.PrimaryScreenWidth * dpiScale;
            var physicalHeight = SystemParameters.PrimaryScreenHeight * dpiScale;

            var byWidth = physicalWidth / 1920;
            var byHeight = physicalHeight / 1080;
            var raw = Math.Min(byWidth, byHeight);

            // 取到 5% 的整数档，避免出现 113% 这种奇怪的默认值
            raw = Math.Round(raw * 20) / 20;

            return NetFxShims.Clamp(raw, 0.8, 1.2);
        }
        catch
        {
            return 1.0;
        }
    }

    // ---------------- 停靠位置 ----------------

    /// <summary>停靠边：底部 / 顶部 / 左侧 / 右侧。</summary>
    public DockEdge Edge
    {
        get => _edge;
        set
        {
            if (!Set(ref _edge, value)) return;

            OnPropertyChanged(nameof(IsVertical));
            OnPropertyChanged(nameof(EdgeMarginThickness));
            OnPropertyChanged(nameof(DockVerticalAlignment));
            OnPropertyChanged(nameof(DockHorizontalAlignment));
            OnPropertyChanged(nameof(DockPanelOrientation));
            OnPropertyChanged(nameof(IconMarginThickness));
            OnPropertyChanged(nameof(IconTransformOrigin));
            OnPropertyChanged(nameof(IndicatorHorizontalAlignment));
            OnPropertyChanged(nameof(IndicatorVerticalAlignment));
            OnPropertyChanged(nameof(IndicatorMarginThickness));
            OnPropertyChanged(nameof(SeparatorWidth));
            OnPropertyChanged(nameof(SeparatorHeight));
            RaiseDerived();
        }
    }

    /// <summary>左右停靠 = 竖排。</summary>
    [JsonIgnore]
    public bool IsVertical => Edge is DockEdge.Left or DockEdge.Right;

    [JsonIgnore]
    public Orientation DockPanelOrientation => IsVertical ? Orientation.Vertical : Orientation.Horizontal;

    /// <summary>玻璃条贴在窗口的哪一侧（配合下面的 Margin 把间距留出来）。</summary>
    [JsonIgnore]
    public VerticalAlignment DockVerticalAlignment => Edge switch
    {
        DockEdge.Bottom => VerticalAlignment.Bottom,
        DockEdge.Top => VerticalAlignment.Top,
        _ => VerticalAlignment.Stretch
    };

    [JsonIgnore]
    public HorizontalAlignment DockHorizontalAlignment => Edge switch
    {
        DockEdge.Left => HorizontalAlignment.Left,
        DockEdge.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Stretch
    };

    /// <summary>
    /// 运行指示条放在图标的外侧（朝屏幕边缘那一侧）：贴底在下、贴顶在上、贴左在左、贴右在右。
    /// 竖排时整条旋转 90°，这样长度动画还是走 Width，不用改动画。
    /// </summary>
    [JsonIgnore]
    public HorizontalAlignment IndicatorHorizontalAlignment => Edge switch
    {
        DockEdge.Left => HorizontalAlignment.Left,
        DockEdge.Right => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Center
    };

    [JsonIgnore]
    public VerticalAlignment IndicatorVerticalAlignment => Edge switch
    {
        DockEdge.Bottom => VerticalAlignment.Bottom,
        DockEdge.Top => VerticalAlignment.Top,
        _ => VerticalAlignment.Center
    };

    /// <summary>指示条落在图标外侧 3.5~7.5px，四个方向一致。</summary>
    [JsonIgnore]
    public Thickness IndicatorMarginThickness => Edge switch
    {
        DockEdge.Top => new Thickness(0, -7.5, 0, 0),
        DockEdge.Left => new Thickness(-7.5, 0, 0, 0),
        DockEdge.Right => new Thickness(0, 0, -7.5, 0),
        _ => new Thickness(0, 0, 0, -7.5)
    };

    /// <summary>分隔线的长边永远 34，只是横竖对调。</summary>
    [JsonIgnore]
    public double SeparatorWidth => IsVertical ? 34 : 1.5;

    [JsonIgnore]
    public double SeparatorHeight => IsVertical ? 1.5 : 34;

    /// <summary>间距要留在所贴的那条边上。</summary>
    [JsonIgnore]
    public Thickness EdgeMarginThickness => Edge switch
    {
        DockEdge.Top => new Thickness(0, BottomMargin, 0, 0),
        DockEdge.Left => new Thickness(BottomMargin, 0, 0, 0),
        DockEdge.Right => new Thickness(0, 0, BottomMargin, 0),
        _ => new Thickness(0, 0, 0, BottomMargin)
    };

    // ---------------- 放大镜 ----------------

    public double MagnifyMaxScale
    {
        get => _magnifyMaxScale;
        set
        {
            if (!Set(ref _magnifyMaxScale, Clamp(value, 1.0, 2.0))) return;
            RaiseDerived();
        }
    }

    /// <summary>放大影响半径，跟着图标尺寸走，保证观感一致。</summary>
    [JsonIgnore]
    public double MagnifySigma => ScaledIconSize * 1.15;

    // ---------------- 派生值（给 XAML 用） ----------------

    /// <summary>窗口高度：留出放大后的余量，否则图标顶部会被窗口裁掉。</summary>
    [JsonIgnore]
    public double WindowHeight => Math.Max(96, ScaledIconSize * MagnifyMaxScale + 34);

    [JsonIgnore]
    public double TileRadius => ScaledIconSize * 0.235;

    /// <summary>图标在磁贴里的内缩，让不同来源的图标看起来大小一致。</summary>
    [JsonIgnore]
    public double IconInset => Math.Max(3, ScaledIconSize * 0.14);

    [JsonIgnore]
    public Thickness IconInsetThickness => new(IconInset);

    /// <summary>图标之间的间距：横排留在左右，竖排留在上下。</summary>
    [JsonIgnore]
    public Thickness IconMarginThickness => IsVertical
        ? new Thickness(0, IconGap / 2, 0, IconGap / 2)
        : new Thickness(IconGap / 2, 0, IconGap / 2, 0);

    /// <summary>
    /// 放大时的锚点：贴底时锚在图标底边（往上长），贴顶时锚在顶边（往下长），
    /// 左右停靠时锚在中心（沿轴向的对称缩放，位移补偿才对得上）。
    /// </summary>
    [JsonIgnore]
    public Point IconTransformOrigin => Edge switch
    {
        DockEdge.Bottom => new Point(0.5, 1),
        DockEdge.Top => new Point(0.5, 0),
        _ => new Point(0.5, 0.5)
    };

    [JsonIgnore]
    public Thickness BottomMarginThickness => new(0, 0, 0, BottomMargin);

    /// <summary>每个磁贴占的横向槽位（含左右外边距）。</summary>
    [JsonIgnore]
    public double TileSlotWidth => ScaledIconSize + IconGap;

    /// <summary>Dock 左右两端的内边距，比图标间距宽一些，玻璃条两端才不显得局促。</summary>
    [JsonIgnore]
    public double SidePadding => IconGap + ScaledIconSize * 0.12;

    /// <summary>
    /// 窗口左右各预留的空白：放大时玻璃条要往外长，窗口宽度必须提前留够，
    /// 否则每帧改窗口宽度会抖。实测整排撑开约 1.8 个磁贴宽，留 3 个磁贴的余量稳妥。
    /// </summary>
    [JsonIgnore]
    public double ExpandReserve => ScaledIconSize * 1.5;

    [JsonIgnore]
    public Thickness DockPaddingThickness => new(SidePadding, 8, SidePadding, 8);

    [JsonIgnore]
    public double PlusFontSize => Math.Max(14, ScaledIconSize * 0.42);

    [JsonIgnore]
    public double TileInnerInset => IconInset;

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(WindowHeight));
        OnPropertyChanged(nameof(TileRadius));
        OnPropertyChanged(nameof(IconInset));
        OnPropertyChanged(nameof(IconInsetThickness));
        OnPropertyChanged(nameof(TileInnerInset));
        OnPropertyChanged(nameof(MagnifySigma));
        OnPropertyChanged(nameof(PlusFontSize));
        OnPropertyChanged(nameof(IconMarginThickness));
        OnPropertyChanged(nameof(DockPaddingThickness));
        OnPropertyChanged(nameof(TileSlotWidth));
        OnPropertyChanged(nameof(SidePadding));
        OnPropertyChanged(nameof(ExpandReserve));
    }

    // ---------------- 系统集成 ----------------

    /// <summary>开机自启（写 HKCU\...\Run）。</summary>
    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!Set(ref _autoStart, value)) return;
            AutoStartService.Set(value);
        }
    }

    /// <summary>
    /// 彻底隐藏原生任务栏。
    /// 不是系统那个「自动隐藏」——鼠标移到屏幕底部也不会弹出来。
    /// </summary>
    public bool HideTaskbar
    {
        get => _hideTaskbar;
        set
        {
            if (!Set(ref _hideTaskbar, value)) return;
            TaskbarService.SetVisible(!value);
        }
    }

    /// <summary>隐藏桌面图标（文件本身不动，只是看不见）。</summary>
    public bool HideDesktopIcons
    {
        get => _hideDesktopIcons;
        set
        {
            if (!Set(ref _hideDesktopIcons, value)) return;
            DesktopIconService.SetVisible(!value);
        }
    }

    // ---------------- 性能 ----------------

    /// <summary>什么时候暂停后台工作。默认不暂停，行为和以前完全一致。</summary>
    public DockPauseMode PauseMode
    {
        get => _pauseMode;
        set => Set(ref _pauseMode, value);
    }

    // ---------------- Dock 图标显示 ----------------

    /// <summary>
    /// 状态胶囊上那个三点品牌图标（点它开设置）。
    /// 0.4.9 起只管胶囊，Dock 上彻底不摆这两颗了。
    /// </summary>
    public bool ShowBrandIcon
    {
        get => _showBrandIcon;
        set => Set(ref _showBrandIcon, value);
    }

    /// <summary>
    /// 状态胶囊上那个四格 Windows 标志（点它弹锁定 / 睡眠 / 关机 / 重启）。
    /// 0.4.9 起只管胶囊。
    /// </summary>
    public bool ShowWindowsIcon
    {
        get => _showWindowsIcon;
        set => Set(ref _showWindowsIcon, value);
    }

    /// <summary>启动台。</summary>
    public bool ShowLaunchpadIcon
    {
        get => _showLaunchpadIcon;
        set => Set(ref _showLaunchpadIcon, value);
    }

    /// <summary>废纸篓。关掉时连同它前面的分隔线一起收起。</summary>
    public bool ShowTrashIcon
    {
        get => _showTrashIcon;
        set => Set(ref _showTrashIcon, value);
    }

    /// <summary>状态胶囊左上角那个「后台程序」按钮。</summary>
    public bool ShowBackgroundAppsButton
    {
        get => _showBackgroundAppsButton;
        set => Set(ref _showBackgroundAppsButton, value);
    }

    /// <summary>
    /// 电池图标右边那个「100%」。默认关 —— 和 macOS 一样只画电池图形，
    /// 具体数字去面板里看。开着的话这一项会占两格（电池 + 文字）。
    /// </summary>
    public bool ShowBatteryPercent
    {
        get => _showBatteryPercent;
        set => Set(ref _showBatteryPercent, value);
    }

    /// <summary>我的电脑（启动台右边那个）。</summary>
    public bool ShowComputerIcon
    {
        get => _showComputerIcon;
        set => Set(ref _showComputerIcon, value);
    }

    /// <summary>废纸篓左边那一排「当前打开的窗口」缩略图。</summary>
    public bool ShowWindowPreviews
    {
        get => _showWindowPreviews;
        set => Set(ref _showWindowPreviews, value);
    }

    /// <summary>
    /// 隐私模式：预览区只显示应用图标，一张缩略图都不抓。
    /// 窗口标题仍会保留（合并预览的窗口列表要用）；密码类应用无论如何都不会被抓。
    /// </summary>
    public bool PreviewIconOnly
    {
        get => _previewIconOnly;
        set => Set(ref _previewIconOnly, value);
    }

    /// <summary>
    /// Dock 和两个胶囊的窗口层级。它们在别的窗口之上还是之下，由这个值决定。
    /// </summary>
    public DockTopMode TopMode
    {
        get => _topMode;
        set => Set(ref _topMode, value);
    }

    // ---------------- 灵动岛（顶部时间胶囊） ----------------

    /// <summary>是否显示灵动岛（顶部时间胶囊）。关掉后整个胶囊不显示。</summary>
    public bool ShowIsland
    {
        get => _showIsland;
        set => Set(ref _showIsland, value);
    }

    /// <summary>
    /// 灵动岛宽度（DIP），只作用于时间形态（默认 88）；播放形态按内容动态拉长。
    /// </summary>
    public double IslandWidth
    {
        get => _islandWidth;
        set => Set(ref _islandWidth, Clamp(value, 60, 500));
    }

    /// <summary>灵动岛高度（DIP），只作用于时间形态（默认 30）。</summary>
    public double IslandHeight
    {
        get => _islandHeight;
        set => Set(ref _islandHeight, Clamp(value, 20, 100));
    }

    /// <summary>
    /// 灵动岛播放形态宽度（DIP）。播放时胶囊会自动拉长到这个宽度（默认 256，
    /// 即自动拉长的基准），在此之上可调宽 / 调窄；曲名放不下会自动省略号。
    /// </summary>
    public double IslandPlayerWidth
    {
        get => _islandPlayerWidth;
        set => Set(ref _islandPlayerWidth, Clamp(value, 150, 500));
    }

    /// <summary>灵动岛播放形态高度（DIP），自动拉长基准为 34，可调。</summary>
    public double IslandPlayerHeight
    {
        get => _islandPlayerHeight;
        set => Set(ref _islandPlayerHeight, Clamp(value, 24, 100));
    }

    /// <summary>灵动岛 X（相对工作区左上角，DIP）。-1 表示自动水平居中。</summary>
    public double IslandX
    {
        get => _islandX;
        set => Set(ref _islandX, value);
    }

    /// <summary>灵动岛 Y（相对工作区左上角，DIP）。-1 表示自动贴顶。</summary>
    public double IslandY
    {
        get => _islandY;
        set => Set(ref _islandY, value);
    }

    /// <summary>true = 固定（默认，不能拖）；false = 可自由拖动位置。</summary>
    public bool IslandFixed
    {
        get => _islandFixed;
        set => Set(ref _islandFixed, value);
    }

    private static double Clamp(double v, double min, double max) =>
        double.IsNaN(v) ? min : Math.Min(max, Math.Max(min, v));

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
