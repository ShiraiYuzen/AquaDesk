using System.Windows;
using System.Windows.Interop;
using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 把窗口变成「点了不抢键盘焦点」的浮层（Dock / 状态胶囊 / 时间胶囊都用它）。
///
/// 光设 `WS_EX_NOACTIVATE` 不够 —— 那只能保证点击时不激活窗口，
/// **WPF 自己还会在鼠标按下时把键盘焦点设到窗口上**。结果就是：
/// 前台仍然是原来的应用，但焦点被我们拿走了，用户回到应用里打字没反应，
/// 而应用已经在前台、Windows 不会重新发激活消息，只能再点一次或走 Dock 图标。
///
/// 补上 `WM_MOUSEACTIVATE → MA_NOACTIVATE`：点击照常送到我们的控件上，
/// 但既不激活窗口、也不动键盘焦点。
/// </summary>
public static class NonActivatingWindow
{
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    /// <param name="clickThrough">连鼠标命中都不要（时间胶囊那种纯展示的浮层）。</param>
    public static void Apply(Window window, bool clickThrough = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = NativeMethods.GetExtendedStyle(hwnd);
        style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        if (clickThrough) style |= NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetExtendedStyle(hwnd, style);

        if (HwndSource.FromHwnd(hwnd) is not { } source)
        {
            App.Log($"非激活窗口：{window.GetType().Name} hwnd={hwnd} 拿不到 HwndSource，挂不上钩子", null);
            return;
        }

        source.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;

            handled = true;
            return MA_NOACTIVATE;
        });

        App.Log($"非激活窗口：{window.GetType().Name} hwnd={hwnd} 样式=0x{style:X} 钩子已挂", null);
    }

    /// <summary>
    /// 运行中切「点击穿透」。
    ///
    /// 时间胶囊平时是纯展示的（穿透，点它等于点在底下窗口上），
    /// 但一旦变成「正在播放」形态，上面有上一首 / 暂停 / 下一首三个按钮，必须收回穿透。
    /// 只改这一个扩展样式位，不用重建窗口，也不用重新挂钩子。
    /// </summary>
    public static void SetClickThrough(Window window, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = NativeMethods.GetExtendedStyle(hwnd);
        var next = clickThrough
            ? style | NativeMethods.WS_EX_TRANSPARENT
            : style & ~NativeMethods.WS_EX_TRANSPARENT;

        if (next != style) NativeMethods.SetExtendedStyle(hwnd, next);
    }
}
