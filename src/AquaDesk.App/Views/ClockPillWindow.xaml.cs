using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AquaDesk.Models;
using AquaDesk.Services;

namespace AquaDesk.Views;

/// <summary>
/// 顶部胶囊（灵动岛那种），两种形态：
///
/// - **时间**：屏幕上方正中间一小条玻璃，只显示 HH:mm。
/// - **正在播放**：系统里有媒体会话在跑（播放中或暂停）时，胶囊横向拉长，
///   露出「应用图标 + 曲名 / 歌手 + 上一首 / 播放暂停 / 下一首」。
///
/// 数据来自 <see cref="MediaSessionService"/>（SMTC），不是抓窗口标题 ——
/// 所以 Spotify、网易云、QQ 音乐、浏览器里的歌都能认，切歌也会自己跟上。
///
/// **两种形态的鼠标行为不一样**：
/// 时间是纯展示，`WS_EX_TRANSPARENT` 穿透，点它等于点在下面的窗口上；
/// 播放形态上有三个按钮，必须收回穿透（但仍是 NOACTIVATE，不抢键盘焦点）。
///
/// **不占工作区**：胶囊是浮层，最大化窗口照样铺到屏幕顶边，它只是盖在上面。
/// </summary>
public partial class ClockPillWindow : Window
{
    /// <summary>胶囊离屏幕顶边的距离（DIP）。</summary>
    private const double TopGap = 6;

    /// <summary>两种形态的尺寸。拉长动画就是在这两个宽度之间补间。</summary>
    private const double ClockWidth = 88;
    private const double PlayerWidth = 256;
    private const double ClockHeight = 30;
    private const double PlayerHeight = 34;

    /// <summary>窗口比胶囊多出来的部分：左右各 10（阴影落点）、上下 6 + 10。</summary>
    private const double ShadowX = 20;
    private const double ShadowY = 16;

    private readonly DispatcherTimer _clockTick = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    private readonly MediaSessionService _media;
    private readonly AppSettings _settings;

    private DispatcherTimer? _widthTween;
    private MediaSnapshot? _snapshot;
    private bool _showingPlayer;

    /// <summary>拖动灵动岛时记下的起点（窗口坐标），用来算位移。</summary>
    private System.Windows.Point? _dragStart;
    private double _dragOriginLeft;
    private double _dragOriginTop;

    public ClockPillWindow(AppSettings settings, MediaSessionService media)
    {
        InitializeComponent();

        // 风格（底色 / 边缘光）直接绑设置，改外观时胶囊跟着变
        DataContext = settings;

        _media = media;
        _settings = settings;

        Loaded += OnLoaded;
        SizeChanged += (_, _) => PositionTopCenter();

        Closed += (_, _) =>
        {
            _clockTick.Stop();
            _widthTween?.Stop();
            _media.Changed -= OnMediaChanged;
            _settings.PropertyChanged -= OnSettingChanged;
        };

        _clockTick.Tick += (_, _) => RefreshClock();
        _media.Changed += OnMediaChanged;
        _settings.PropertyChanged += OnSettingChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 时间形态默认点击穿透；但「可拖动」时要收回穿透才能接住鼠标。
        // 固定（默认）→ 穿透，点它等于点在下面的窗口；可拖动 → 不穿透。
        NonActivatingWindow.Apply(this, clickThrough: _settings.IslandFixed);

        ApplyCustomSize();
        RefreshClock();
        PositionTopCenter();
        ApplySnapshot(_media.Current);

        _clockTick.Start();

        // 关掉灵动岛：初始化完再隐藏，保证之后重新打开时窗口已经就绪
        if (!_settings.ShowIsland) Visibility = Visibility.Collapsed;
    }

    /// <summary>把自定义宽高套到时间 / 播放两种形态上（-1 表示跟随默认内容尺寸）。</summary>
    private void ApplyCustomSize()
    {
        // 时间形态的自定义宽高在 SetPillWidth/SetPillHeight 里应用（那里是尺寸的唯一入口）
        SetPillWidth(_showingPlayer ? PlayerWidth : ClockWidth);
        SetPillHeight(_showingPlayer ? PlayerHeight : ClockHeight);
    }

    private void RefreshClock() => ClockText.Text = DateTime.Now.ToString("HH:mm");

    /// <summary>默认位置：水平居中、贴屏幕顶边留 6px。用户拖过（IslandX/IslandY ≥ 0）就停在那。</summary>
    private void PositionTopCenter()
    {
        if (ActualWidth <= 0) return;

        var wa = SystemParameters.WorkArea;

        Left = _settings.IslandX >= 0
            ? wa.Left + _settings.IslandX
            : wa.Left + (wa.Width - ActualWidth) / 2;

        Top = _settings.IslandY >= 0
            ? wa.Top + _settings.IslandY
            : wa.Top + TopGap;

        App.Trace($"时间胶囊：{Left:F0},{Top:F0} {ActualWidth:F0}×{ActualHeight:F0}" +
                  $"（Pill={Pill.ActualWidth:F0} 设定={Pill.Width:F0} 形态={(_showingPlayer ? "播放" : "时间")}）");
    }

    // ---------------- 正在播放 ----------------

    /// <summary>媒体会话在后台线程上报，切回 UI 线程再动界面。</summary>
    private void OnMediaChanged(MediaSnapshot? snapshot) =>
        Dispatcher.BeginInvoke(new Action(() => ApplySnapshot(snapshot)), DispatcherPriority.Background);

    private void ApplySnapshot(MediaSnapshot? snapshot)
    {
        _snapshot = snapshot;

        var hasTrack = snapshot is { Title.Length: > 0 };

        // 内容先刷，形态再切 —— 这样从「时间」直接变成「在播」时不会闪一帧空文本
        if (hasTrack) UpdatePlayer(snapshot!);

        if (hasTrack == _showingPlayer) return;

        _showingPlayer = hasTrack;
        if (hasTrack) EnterPlayer();
        else LeavePlayer();
    }

    private void UpdatePlayer(MediaSnapshot snapshot)
    {
        TrackTitle.Text = snapshot.Title;
        TrackArtist.Text = snapshot.Artist;
        TrackArtist.Visibility = string.IsNullOrWhiteSpace(snapshot.Artist)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (snapshot.Icon is not null)
        {
            AppIcon.Source = snapshot.Icon;
            AppIcon.Visibility = Visibility.Visible;
            AppIconFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            AppIcon.Source = null;
            AppIcon.Visibility = Visibility.Collapsed;
            AppIconFallback.Visibility = Visibility.Visible;
        }

        SetPlayGlyph(snapshot.IsPlaying);

        PrevButton.IsEnabled = snapshot.CanPrevious;
        PlayPauseButton.IsEnabled = snapshot.CanPlayPause;
        NextButton.IsEnabled = snapshot.CanNext;
    }

    private void SetPlayGlyph(bool playing)
    {
        PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        PlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PlayPauseButton.ToolTip = playing ? "暂停" : "播放";
    }

    /// <summary>展开：先把内容摆上（透明），把胶囊拉长，拉完再淡入 —— 免得文字溢出到玻璃外面。</summary>
    private void EnterPlayer()
    {
        PlayerState.BeginAnimation(OpacityProperty, null);
        PlayerState.Opacity = 0;
        PlayerState.Visibility = Visibility.Visible;
        ClockText.Visibility = Visibility.Collapsed;

        SetPillHeight(PlayerHeight);

        // 有按钮了，必须能接收点击
        NonActivatingWindow.SetClickThrough(this, false);

        TweenWidth(PlayerWidth, () => FadeTo(PlayerState, 1, 120));
    }

    /// <summary>收起：先淡出内容，再缩短、还原成时间。</summary>
    private void LeavePlayer()
    {
        FadeTo(PlayerState, 0, 110, () =>
        {
            PlayerState.Visibility = Visibility.Collapsed;
            ClockText.Visibility = Visibility.Visible;
            SetPillHeight(ClockHeight);

            // 回到纯展示：固定则重新穿透，可拖动则保持不穿透（要接住鼠标）
            NonActivatingWindow.SetClickThrough(this, _settings.IslandFixed);

            RefreshClock();
            TweenWidth(ClockWidth);
        });
    }

    /// <summary>
    /// 宽度补间。窗口是 SizeToContent，Pill 一变宽窗口跟着长，居中定位在 SizeChanged 里重算，
    /// 所以看起来就是「从中间往两边拉开」。
    /// </summary>
    private void TweenWidth(double target, Action? done = null)
    {
        _widthTween?.Stop();

        var from = Pill.Width;
        if (double.IsNaN(from) || from <= 0) from = target;

        if (Math.Abs(from - target) < 0.5)
        {
            SetPillWidth(target);
            done?.Invoke();
            return;
        }

        var started = Environment.TickCount64;
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };

        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp((Environment.TickCount64 - started) / 190.0, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);      // ease-out cubic，起步快、收尾稳

            SetPillWidth(from + (target - from) * eased);

            if (t < 1) return;

            timer.Stop();
            SetPillWidth(target);
            done?.Invoke();
        };

        timer.Start();
        _widthTween = timer;
    }

    /// <summary>胶囊宽度和窗口宽度一起改 —— 窗口得给两边各留 10px 的阴影落点。</summary>
    private void SetPillWidth(double width)
    {
        // 自定义宽度只作用于时间形态；播放形态要容纳曲名和按钮，按内容动态拉长
        if (!_showingPlayer) width = _settings.IslandWidth;

        Pill.Width = width;
        Width = width + ShadowX;
    }

    private void SetPillHeight(double height)
    {
        // 自定义高度只作用于时间形态
        if (!_showingPlayer) height = _settings.IslandHeight;

        Pill.Height = height;
        Height = height + ShadowY;
    }

    private static void FadeTo(UIElement element, double to, int milliseconds, Action? done = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        if (done is not null) animation.Completed += (_, _) => done();

        element.BeginAnimation(OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    // ---------------- 按钮 ----------------

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        _media.TogglePlayPause();

        // 乐观切换字形：真状态要等下一轮轮询（≤600ms），但这 600ms 里按钮不该毫无反应
        SetPlayGlyph(!(_snapshot?.IsPlaying ?? false));
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => _media.Previous();

    private void OnNext(object sender, RoutedEventArgs e) => _media.Next();

    // ---------------- 灵动岛：拖动 / 设置响应 ----------------

    /// <summary>设置页改了灵动岛相关项，这里实时跟上。</summary>
    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ShowIsland):
                // 关掉整个隐藏；打开时重新摆好（位置 / 尺寸 / 穿透都按最新设置来）
                if (!_settings.ShowIsland)
                {
                    Visibility = Visibility.Collapsed;
                }
                else
                {
                    Visibility = Visibility.Visible;
                    NonActivatingWindow.SetClickThrough(this, _settings.IslandFixed && !_showingPlayer);
                    ApplyCustomSize();
                    PositionTopCenter();
                }
                break;

            case nameof(AppSettings.IslandWidth):
            case nameof(AppSettings.IslandHeight):
                ApplyCustomSize();
                PositionTopCenter();
                break;

            case nameof(AppSettings.IslandX):
            case nameof(AppSettings.IslandY):
                PositionTopCenter();
                break;

            case nameof(AppSettings.IslandFixed):
                // 固定 ↔ 可拖动：切换穿透。播放形态永远要能点按钮，不穿透。
                NonActivatingWindow.SetClickThrough(this, _settings.IslandFixed && !_showingPlayer);
                break;
        }
    }

    /// <summary>把灵动岛拖到用户松手的位置，坐标写回设置（持久化）。</summary>
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_settings.IslandFixed || _showingPlayer) return;   // 固定 / 播放形态不可拖

        _dragStart = e.GetPosition(this);
        _dragOriginLeft = Left;
        _dragOriginTop = Top;
        CaptureMouse();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not System.Windows.Point start) return;

        var now = e.GetPosition(this);
        Left = _dragOriginLeft + (now.X - start.X);
        Top = _dragOriginTop + (now.Y - start.Y);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStart is null) return;

        _dragStart = null;
        ReleaseMouseCapture();

        // 松手才把坐标写回设置（相对工作区左上角），并 clamp 进屏幕、重新摆正
        var wa = SystemParameters.WorkArea;
        _settings.IslandX = Math.Clamp(Left - wa.Left, 0, wa.Width - ActualWidth);
        _settings.IslandY = Math.Clamp(Top - wa.Top, 0, wa.Height - ActualHeight);
        PositionTopCenter();
    }

    /// <summary>恢复默认：位置回到顶部居中、尺寸回到时间形态默认（88×30）。</summary>
    public void ResetIslandPosition()
    {
        _settings.IslandX = -1;
        _settings.IslandY = -1;
        _settings.IslandWidth = 88;
        _settings.IslandHeight = 30;

        ApplyCustomSize();
        PositionTopCenter();
    }
}
