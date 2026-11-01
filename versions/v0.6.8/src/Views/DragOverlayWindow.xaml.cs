using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AquaDesk.Core;

namespace AquaDesk.Views;

/// <summary>
/// 拖动时跟着指针的小浮层：一个半透明的图标，外加拖出 Dock 2 秒后出现的「移除」提示。
/// 独立窗口，可以移到屏幕外，不受 Dock 窗口边界和 Popup 的边界修正影响。
/// </summary>
public partial class DragOverlayWindow : Window
{
    // 浮层画布里的锚点：图标中心就落在这里，上方留出空间放提示
    private const double AnchorX = 200;
    private const double AnchorY = 240;

    public DragOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    /// <summary>不抢焦点、不参与 Alt+Tab、鼠标直接穿透到底下的窗口。</summary>
    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = NativeMethods.GetExtendedStyle(hwnd)
                    | NativeMethods.WS_EX_NOACTIVATE
                    | NativeMethods.WS_EX_TOOLWINDOW
                    | NativeMethods.WS_EX_TRANSPARENT;

        NativeMethods.SetExtendedStyle(hwnd, style);
    }

    public void ShowItem(ImageSource? icon, double iconSize)
    {
        Ghost.Source = icon;
        Ghost.Width = iconSize;
        Ghost.Height = iconSize;

        Canvas.SetLeft(Ghost, AnchorX - iconSize / 2);
        Canvas.SetTop(Ghost, AnchorY - iconSize / 2);

        Hint.Visibility = Visibility.Collapsed;
    }

    /// <summary>显示前先按 DIP 摆个大概位置，免得窗口在屏幕中间闪一下；Show 之后再 MoveTo 精确对齐。</summary>
    public void PlaceBeforeShow(double screenX, double screenY)
    {
        Left = screenX - AnchorX;
        Top = screenY - AnchorY;
    }

    /// <summary>
    /// 把浮层挪到屏幕坐标（DIP）对应的位置，图标中心对准指针。
    /// 用 SetWindowPos 直接给物理像素：WPF 的 Left/Top 要经过一次 DIP 换算，
    /// 换算过程中窗口会被重新布局，位置对不准（实测偏了好几百像素）。
    /// </summary>
    public void MoveTo(double screenX, double screenY)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = (int)Math.Round((screenX - AnchorX) * dpi.DpiScaleX);
        var y = (int)Math.Round((screenY - AnchorY) * dpi.DpiScaleY);

        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    public void ShowRemoveHint(double iconSize)
    {
        Hint.Visibility = Visibility.Visible;
        Hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Canvas.SetLeft(Hint, AnchorX - Hint.DesiredSize.Width / 2);
        Canvas.SetTop(Hint, AnchorY - iconSize / 2 - Hint.DesiredSize.Height - 8);
    }

    public void HideRemoveHint() => Hint.Visibility = Visibility.Collapsed;
}
