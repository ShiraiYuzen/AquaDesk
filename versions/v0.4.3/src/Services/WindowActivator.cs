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

        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            EnsureFocus(hwnd);
            return true;
        }

        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();

        var attached = targetThread != 0
                       && targetThread != currentThread
                       && NativeMethods.AttachThreadInput(currentThread, targetThread, true);

        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            var ok = NativeMethods.SetForegroundWindow(hwnd);

            if (!ok || NativeMethods.GetForegroundWindow() != hwnd)
            {
                // Alt+Tab 那个入口，不受前台锁限制
                NativeMethods.SwitchToThisWindow(hwnd, true);
            }
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }

        // 焦点在脱离之后再补：挂在别的线程上强设焦点，会把应用自己的输入控件焦点顶掉，
        // 表现就是「窗口到了最前面，但打字没反应，得再点一下」。
        EnsureFocus(hwnd);

        var foreground = NativeMethods.GetForegroundWindow();
        App.Log($"激活窗口：目标={hwnd} 前台={foreground} 成功={foreground == hwnd}", null);

        return foreground == hwnd;
    }

    /// <summary>
    /// 只在「目标线程压根没有焦点窗口」时补一次焦点。
    /// 应用自己已经恢复好焦点时不要动它 —— 顶层框架拿到焦点等于把输入焦点抢走了。
    /// </summary>
    private static void EnsureFocus(IntPtr hwnd)
    {
        var thread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (thread == 0) return;

        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(thread, ref info)) return;
        if (info.hwndFocus != IntPtr.Zero) return;

        var target = info.hwndActive != IntPtr.Zero ? info.hwndActive : hwnd;
        var current = NativeMethods.GetCurrentThreadId();

        var attached = thread != current
                       && NativeMethods.AttachThreadInput(current, thread, true);

        try
        {
            NativeMethods.SetFocus(target);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(current, thread, false);
        }
    }
}
