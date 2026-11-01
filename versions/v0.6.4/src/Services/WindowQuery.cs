using System.Diagnostics;
using System.Runtime.InteropServices;
using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>查当前前台窗口属于哪个进程，用来判断 Dock 上哪个应用「正在被使用」。</summary>
public static class WindowQuery
{
    public static string? GetForegroundProcessName()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0) return null;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            // 进程刚好退出、或权限不够，都不该影响 Dock
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    /// <summary>当前前台窗口是不是本进程的（用来判断「用户是不是在用 AquaDesk」）。</summary>
    public static bool IsOwnProcessForeground()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            return processId == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 前台窗口是不是全屏。<paramref name="borderless"/> 表示它同时没有标题栏
    /// （游戏那种 WS_POPUP 全屏；浏览器 F11 仍然带 WS_CAPTION）。
    ///
    /// 判定条件是「窗口矩形盖满整个显示器」+「不是最大化状态」——
    /// 普通窗口最大化后矩形其实也会盖满显示器（不可见的边框伸到了屏幕外），
    /// 只能靠 <c>IsZoomed</c> 把它排除掉。
    /// </summary>
    public static bool IsForegroundFullscreen(out bool borderless)
    {
        borderless = false;

        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            // 桌面和任务栏本身就是铺满屏幕的，不能算「全屏应用」
            if (IsShellWindow(hwnd)) return false;

            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return false;

            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };

            if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return false;

            var screen = info.rcMonitor;

            var covers = rect.Left <= screen.Left && rect.Top <= screen.Top
                         && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;

            if (!covers) return false;
            if (NativeMethods.IsZoomed(hwnd)) return false;

            borderless = (NativeMethods.GetStyle(hwnd) & NativeMethods.WS_CAPTION) == 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 前台窗口是不是「占满屏幕」：最大化，或者全屏（F11 / 无边框游戏）。
    ///
    /// Dock 和两个胶囊在这两种情况下都该沉到最底层 —— 它们是盖在所有窗口之上的浮层，
    /// 窗口一最大化就会压住别人的内容（浏览器底部那条工具栏、编辑器最后一行代码都被吃掉）。
    /// </summary>
    /// <summary>
    /// 前台窗口是不是「占满屏幕」：最大化，或者全屏（F11 / 无边框游戏）。
    ///
    /// Dock 和状态胶囊在这两种情况下都该沉到最底层 —— 它们是盖在所有窗口之上的浮层，
    /// 窗口一最大化就会压住别人的内容。
    ///
    /// 前台窗口本身没放大时，再退一步看「前台进程有没有放大的顶层窗口」——
    /// WPS、部分编辑器会把键盘焦点留在某个子窗口 / 悬浮窗上，真正放大铺满的文档窗口
    /// 并不是前台窗口，光查前台就会漏掉，导致浮层一直压在别人内容上面。
    /// </summary>
    public static bool IsForegroundCoveringScreen()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            if (IsShellWindow(hwnd)) return false;

            if (NativeMethods.IsZoomed(hwnd) || IsForegroundFullscreen(out _)) return true;

            // 退路：前台窗口没放大，但同进程有放大的顶层窗口（WPS 这类多窗口应用）
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            return ProcessCoversScreen(pid);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 某个进程有没有「可见、放大（IsZoomed）」的顶层窗口。
    /// 用来兜住前台焦点不在放大窗口上的情况（WPS 这类多窗口应用）。
    ///
    /// 这里不要求「盖满整个显示器」：放大的窗口本来就会填满工作区，
    /// 而原生任务栏可见时，最大化窗口底部会留出任务栏那一条（约 40px），
    /// 矩形并不等于整个显示器——若加上全屏矩形判定反而会漏掉，
    /// 浮层就一直压在别人内容上。前台窗口那条路径也是靠 IsZoomed 直接判定，
    /// 这里保持一致：IsZoomed 即视为「占满屏幕」。
    /// </summary>
    private static bool ProcessCoversScreen(uint pid)
    {
        if (pid == 0) return false;

        var found = false;

        NativeMethods.EnumWindows((w, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(w, out var wp);
            if (wp != pid) return true;
            if (!NativeMethods.IsWindowVisible(w)) return true;
            if (IsShellWindow(w)) return true;
            if (NativeMethods.IsZoomed(w))
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// 桌面 / 任务栏这些 Shell 窗口铺满屏幕是本职，不能当「全屏应用」看。
    /// 缓冲区复用：这条判断挂在 120ms 的 z 序轮询上，每秒要跑八次，
    /// 每次 new 一个 StringBuilder 纯属白给 GC 添活。
    /// </summary>
    [ThreadStatic]
    private static System.Text.StringBuilder? _classNameBuffer;

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var className = _classNameBuffer ??= new System.Text.StringBuilder(256);
        className.Clear();

        if (NativeMethods.GetClassNameW(hwnd, className, className.Capacity) <= 0) return false;

        return className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>
    /// 回收站是不是开着。
    /// 它没有独立进程（就是 explorer 的一个窗口），只能按窗口标题认，
    /// 中文系统是「回收站」，英文是「Recycle Bin」。
    /// </summary>
    public static bool IsTrashOpen() => FindTrashWindow() != IntPtr.Zero;

    /// <summary>回收站窗口是不是当前前台窗口（用来让指示条变长）。</summary>
    public static bool IsTrashForeground() => IsTrashWindow(NativeMethods.GetForegroundWindow());

    /// <summary>
    /// 点废纸篓的三态，和普通应用一致：
    /// 没开 → 返回 false（调用方去打开）；开着且在前台 → 最小化；在后台 → 提到前台。
    /// </summary>
    public static bool ToggleTrash()
    {
        var hwnd = FindTrashWindow();
        if (hwnd == IntPtr.Zero) return false;

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(hwnd);
            return true;
        }

        if (IsTrashForeground())
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            return true;
        }

        NativeMethods.SetForegroundWindow(hwnd);
        return true;
    }

    /// <summary>关掉所有回收站窗口。</summary>
    public static void CloseTrash()
    {
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (IsTrashWindow(hwnd))
            {
                NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            return true;
        }, IntPtr.Zero);
    }

    private static IntPtr FindTrashWindow()
    {
        var found = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsTrashWindow(hwnd)) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static bool IsTrashWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd)) return false;

        var className = new System.Text.StringBuilder(256);
        if (NativeMethods.GetClassNameW(hwnd, className, className.Capacity) == 0) return false;
        if (className.ToString() != "CabinetWClass") return false;

        var title = new System.Text.StringBuilder(512);
        if (GetWindowTextW(hwnd, title, title.Capacity) == 0) return false;

        var text = title.ToString();

        return text.Contains("回收站", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Recycle Bin", StringComparison.OrdinalIgnoreCase);
    }
}
