using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace AquaDesk.Services;

/// <summary>
/// 菜单开着时挂一个低级鼠标钩子：**任何一次鼠标按下**（不管多快、落在哪个进程的窗口上），
/// 只要位置不在菜单矩形里，就关掉菜单。
///
/// 为什么不用轮询：Dock 是 `WS_EX_NOACTIVATE` 窗口，菜单不会因为「窗口失活」自动关闭，
/// 所以原来靠 60ms 轮询 `GetAsyncKeyState`。问题是快速点击（按下→松开不到 60ms）
/// 可能整段落在两次采样之间，一次都没被看到 —— 用户点了别的应用，菜单还赖着不走。
/// 钩子是事件驱动的，一次都不会漏。
/// </summary>
public static class MenuDismissHook
{
    private const int WH_MOUSE_LL = 14;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    // 委托必须留住引用：被 GC 回收后系统再回调就是野指针
    private static HookProc? _callback;
    private static IntPtr _hook;

    private static Func<Rect?>? _bounds;
    private static Action? _dismiss;

    /// <summary>挂上钩子。<paramref name="bounds"/> 返回菜单的屏幕矩形（**物理像素**），拿不到就返回 null。</summary>
    public static void Install(Func<Rect?> bounds, Action dismiss)
    {
        Uninstall();

        _bounds = bounds;
        _dismiss = dismiss;
        _callback = OnMouse;

        _hook = SetWindowsHookExW(WH_MOUSE_LL, _callback, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            App.Log("菜单关闭钩子安装失败，退回轮询", null);
        }
    }

    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _bounds = null;
        _dismiss = null;
        _callback = null;
    }

    private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _bounds is not null && _dismiss is not null)
        {
            var message = (int)wParam;
            if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var bounds = _bounds();

                // 拿不到菜单矩形时不判断，免得「点菜单里也把它关掉」
                if (bounds is { } rect
                    && (info.Point.X < rect.Left || info.Point.X > rect.Right
                        || info.Point.Y < rect.Top || info.Point.Y > rect.Bottom))
                {
                    var dismiss = _dismiss;
                    Application.Current?.Dispatcher.BeginInvoke(dismiss, DispatcherPriority.Send);
                }
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}
