using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AquaDesk.Models;

namespace AquaDesk.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = settings;

        // 无边框窗口没有系统关闭按钮，Esc 就是最自然的退出方式
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        // 小屏 / 4:3 屏上 828×648 可能装不下，先按工作区收一下再居中
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;

            Width = Math.Min(Width, area.Width - 40);
            Height = Math.Min(Height, area.Height - 40);

            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        };
    }

    /// <summary>
    /// 无边框窗口靠标题区拖动。DragMove 在鼠标提前松开时会抛 InvalidOperationException，
    /// 这是 WPF 的老毛病，吞掉即可。
    /// </summary>
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 松手太快，忽略
        }
    }

    private void OnStyleLiquid(object sender, RoutedEventArgs e) => _settings.Style = DockStyle.Liquid;

    private void OnStyleClassic(object sender, RoutedEventArgs e) => _settings.Style = DockStyle.Classic;

    // ---------------- 窗口按钮 ----------------

    private const double DefaultWidth = 828;
    private const double DefaultHeight = 648;

    private bool _zoomed;

    /// <summary>外层卡片的投影，满屏时要摘掉再装回去。</summary>
    private System.Windows.Media.Effects.Effect? _cardShadow;

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnZoom(object sender, RoutedEventArgs e)
    {
        if (_zoomed) RestoreWindow();
        else MaximizeToScreen();
    }

    /// <summary>
    /// 铺满工作区。
    ///
    /// 不能用「整块屏幕」：顶部菜单栏现在是 AppBar，占了 26px，窗口顶到 0 会把红绿灯按钮
    /// 藏在菜单栏底下。工作区正好是「菜单栏下方 → 屏幕底边」（任务栏隐藏时底边就是屏幕底），
    /// 所以按工作区铺满才是视觉上的全屏。
    /// </summary>
    private void MaximizeToScreen()
    {
        var area = SystemParameters.WorkArea;

        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;

        RootCard.Margin = new Thickness(0);

        // 满屏时阴影没地方落，留着只会白占一张全屏大小的中间位图
        _cardShadow ??= RootCard.Effect;
        RootCard.Effect = null;

        ApplyCornerRadius(0);
        _zoomed = true;
    }

    private void RestoreWindow()
    {
        var area = SystemParameters.WorkArea;

        Width = DefaultWidth;
        Height = DefaultHeight;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;

        RootCard.Margin = new Thickness(14);
        RootCard.Effect = _cardShadow;

        ApplyCornerRadius(13);
        _zoomed = false;
    }

    /// <summary>外层卡片和模板里那两块内层 Border 的圆角要一起改，不然满屏时四个角对不上。</summary>
    private void ApplyCornerRadius(double radius)
    {
        RootCard.CornerRadius = new CornerRadius(radius);

        if (RootTabs.Template?.FindName("SidebarBorder", RootTabs) is Border sidebar)
        {
            sidebar.CornerRadius = new CornerRadius(radius, 0, 0, radius);
        }

        if (RootTabs.Template?.FindName("ContentBorder", RootTabs) is Border content)
        {
            content.CornerRadius = new CornerRadius(0, radius, radius, 0);
        }
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        // 逐个属性写回，保证绑定链路不被打断（换实例的话 DataContext 就脱钩了）
        var defaults = new AppSettings();
        _settings.GlassOpacity = defaults.GlassOpacity;
        _settings.CornerRadius = defaults.CornerRadius;
        _settings.IconSize = defaults.IconSize;
        _settings.UiScale = defaults.UiScale;
        _settings.IconGap = defaults.IconGap;
        _settings.BottomMargin = defaults.BottomMargin;
        _settings.MagnifyMaxScale = defaults.MagnifyMaxScale;
        _settings.ShowBrandIcon = defaults.ShowBrandIcon;
        _settings.ShowWindowsIcon = defaults.ShowWindowsIcon;
        _settings.ShowLaunchpadIcon = defaults.ShowLaunchpadIcon;
        _settings.ShowTrashIcon = defaults.ShowTrashIcon;
        _settings.ShowComputerIcon = defaults.ShowComputerIcon;
        _settings.ShowWindowPreviews = defaults.ShowWindowPreviews;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
