using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 把别的应用的窗口「真正」切到前台。
///
/// Dock 是 WS_EX_NOACTIVATE 窗口，点它本进程不会成为前台进程，所以不能直接
/// SetForegroundWindow —— 前台锁会拦。而且**只调 SetForegroundWindow 还不够**：
/// 窗口会被画到最前面，但键盘焦点仍留在原来那，用户还得再点一下才能输入，
/// 看起来就是「窗口在最上面却像在后台」。
///
/// 正确做法是挂到**目标窗口自己的线程**上（不是当前前台线程，挂错线程时
/// SetForegroundWindow 会返回成功但前台根本不切换），然后依次：
/// BringWindowToTop → SetForegroundWindow → SetFocus。
/// </summary>
public static class WindowActivator
{
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        if (NativeMethods.GetForegroundWindow() == hwnd) return true;

        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();

        var attached = targetThread != 0
                       && targetThread != currentThread
                       && NativeMethods.AttachThreadInput(currentThread, targetThread, true);

        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            var ok = NativeMethods.SetForegroundWindow(hwnd);

            // 焦点必须单独给：不挂线程时这句对别的线程的窗口无效
            if (attached) NativeMethods.SetFocus(hwnd);

            if (!ok || NativeMethods.GetForegroundWindow() != hwnd)
            {
                // Alt+Tab 那个入口，不受前台锁限制
                NativeMethods.SwitchToThisWindow(hwnd, true);
            }

            return NativeMethods.GetForegroundWindow() == hwnd;
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }
}
