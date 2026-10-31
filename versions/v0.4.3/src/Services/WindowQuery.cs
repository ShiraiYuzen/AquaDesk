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
            var className = new System.Text.StringBuilder(256);
            if (NativeMethods.GetClassNameW(hwnd, className, className.Capacity) > 0)
            {
                var name = className.ToString();

                if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                {
                    return false;
                }
            }

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
