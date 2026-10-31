using System.Runtime.InteropServices;
using System.Windows;
using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 把菜单栏注册成屏幕顶部的 AppBar，占住它自己那一条高度。
///
/// 目的只有一个：让最大化 / 贴边的窗口从菜单栏下方开始，而不是钻到菜单栏底下去。
/// Dock 不这么做 —— 它要浮在窗口上面（见 DockWindow.PositionDock 的注释）。
///
/// 用 AppBar 而不是 SPI_SETWORKAREA：进程崩了 Shell 会自动撤销预留，
/// 不会留下一条永远缺着的桌面。
/// </summary>
public static class AppBarService
{
    private static IntPtr _hwnd;
    private static bool _registered;

    public static bool Register(IntPtr hwnd)
    {
        if (_registered && _hwnd == hwnd) return true;

        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = hwnd,
            // ABM_NEW 要求给个回调消息，但我们不需要响应任何通知
            uCallbackMessage = 0x0400 + 101
        };

        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data) == IntPtr.Zero)
        {
            return false;
        }

        _hwnd = hwnd;
        _registered = true;
        return true;
    }

    public static void Remove()
    {
        if (!_registered) return;

        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _hwnd
        };

        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);

        _registered = false;
        _hwnd = IntPtr.Zero;
    }

    /// <summary>
    /// 占住屏幕顶部 <paramref name="heightDip"/> 这么高的一条，
    /// 返回 Shell 实际批下来的矩形（**DIP**，和 WPF 坐标系一致）。
    /// 系统任务栏也在顶部时，QUERYPOS 会把我们挪到它下面。
    /// </summary>
    public static Rect ReserveTop(double heightDip, double dpiScaleX, double dpiScaleY)
    {
        var screenWidth = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpiScaleX);
        var reserve = (int)Math.Round(Math.Max(1, heightDip) * dpiScaleY);

        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = _hwnd,
            uEdge = NativeMethods.ABE_TOP
        };

        data.rc = new NativeMethods.RECT
        {
            Left = 0,
            Top = 0,
            Right = screenWidth,
            Bottom = reserve
        };

        // QUERYPOS 只负责避开任务栏，回来之后高度要自己再钉一次（AppBar 的标准写法）
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);
        data.rc.Bottom = data.rc.Top + reserve;

        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);

        return new Rect(
            data.rc.Left / dpiScaleX,
            data.rc.Top / dpiScaleY,
            (data.rc.Right - data.rc.Left) / dpiScaleX,
            (data.rc.Bottom - data.rc.Top) / dpiScaleY);
    }
}
