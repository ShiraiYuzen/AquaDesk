using System.Diagnostics;
using AquaDesk.Core;
using AquaDesk.Models;

namespace AquaDesk.Services;

public enum DockClickAction
{
    /// <summary>本来没在跑，这次是启动。</summary>
    Launched,

    /// <summary>在前台，这次最小化了。</summary>
    Minimized,

    /// <summary>在后台或已最小化，这次提到前台。</summary>
    Restored,

    /// <summary>找不到窗口也启动失败。</summary>
    Failed
}

/// <summary>
/// 点 Dock 图标的三态行为：没开 → 启动；开着且在前台 → 最小化；开着但在后台 → 提到前台。
/// 和 macOS Dock 的点击语义一致。
/// </summary>
public static class AppWindowController
{
    public static DockClickAction Toggle(DockItem item)
    {
        var hwnd = FindMainWindow(item.ProcessName);

        // 找不到窗口：要么真没开，要么最小化到托盘了。交给 Shell 去启动/唤起。
        if (hwnd == IntPtr.Zero)
        {
            App.Log($"点击「{item.Name}」：进程={item.ProcessName ?? "(空)"} 找不到窗口 → 启动", null);
            item.Launch();
            return DockClickAction.Launched;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            App.Log($"点击「{item.Name}」：hwnd={hwnd} 已最小化 → 还原", null);
            Restore(hwnd);
            return DockClickAction.Restored;
        }

        if (IsForeground(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
            App.Log($"点击「{item.Name}」：hwnd={hwnd} 在前台 → 最小化", null);
            return DockClickAction.Minimized;
        }

        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var foregroundPid);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var targetPid);

        App.Log($"点击「{item.Name}」：hwnd={hwnd} 不在前台（前台 pid={foregroundPid} 目标 pid={targetPid}）→ 提到前台", null);

        Restore(hwnd);
        return DockClickAction.Restored;
    }

    /// <summary>把应用的主窗口最小化（右键菜单用）。</summary>
    public static void Minimize(DockItem item)
    {
        var hwnd = FindMainWindow(item.ProcessName);
        if (hwnd == IntPtr.Zero) return;
        if (NativeMethods.IsIconic(hwnd)) return;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
    }

    /// <summary>
    /// 找这个进程名对应的「真正的应用窗口」。
    /// 不能直接用 Process.MainWindowHandle：explorer 这类进程的主窗口可能是任务栏或桌面，
    /// 拿它去最小化会把整个 shell 收起来。所以统一枚举顶层窗口，再按窗口类排除系统外壳。
    /// </summary>
    private static IntPtr FindMainWindow(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return IntPtr.Zero;

        var processIds = new HashSet<uint>();

        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                processIds.Add((uint)process.Id);
                process.Dispose();
            }
        }
        catch
        {
            return IntPtr.Zero;
        }

        if (processIds.Count == 0) return IntPtr.Zero;

        var candidates = new List<(IntPtr Hwnd, long Area)>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
            if (!processIds.Contains(owner)) return true;

            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.GetWindowTextLengthW(hwnd) == 0) return true;

            var extended = NativeMethods.GetExtendedStyle(hwnd);
            if ((extended & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            // **WS_EX_NOACTIVATE 的窗口不能当目标**：Electron / Chromium 系应用
            // （WorkBuddy、VS Code、Discord…）都会建几个「有标题、可见、但永远激活不了」的辅助窗口。
            // 选中这种窗口去 SetForegroundWindow，API 返回 true，前台却纹丝不动 ——
            // 表现就是「点图标没反应」。
            if ((extended & NativeMethods.WS_EX_NOACTIVATE) != 0) return true;

            if (IsShellWindow(hwnd)) return true;

            NativeMethods.GetWindowRect(hwnd, out var rect);
            var area = (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);

            candidates.Add((hwnd, area));
            return true;
        }, IntPtr.Zero);

        if (candidates.Count == 0) return IntPtr.Zero;

        // 有多个窗口时优先选当前前台那个；否则取**面积最大**的 —— 那才是用户眼里的主窗口，
        // 取第一个经常抓到某个后台小窗口。
        var foreground = NativeMethods.GetForegroundWindow();
        foreach (var (hwnd, _) in candidates)
        {
            if (hwnd == foreground) return hwnd;
        }

        var best = candidates[0];
        foreach (var candidate in candidates)
        {
            if (candidate.Area > best.Area) best = candidate;
        }

        return best.Hwnd;
    }

    /// <summary>任务栏、桌面、通知区域这些系统外壳窗口，绝不能拿来当"应用窗口"。</summary>
    private static readonly string[] ShellWindowClasses =
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "NotifyIconOverflowWindow",
        "TaskListThumbnailWnd",
        "Windows.UI.Core.CoreWindow"
    };

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        if (NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity) == 0) return false;

        var className = buffer.ToString();

        foreach (var shellClass in ShellWindowClasses)
        {
            if (string.Equals(className, shellClass, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsForeground(IntPtr hwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == hwnd) return true;
        if (foreground == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var targetPid);

        return foregroundPid == targetPid;
    }

    /// <summary>把窗口提到前台并交给它键盘焦点，细节见 <see cref="WindowActivator"/>。</summary>
    private static void Restore(IntPtr hwnd)
    {
        var ok = WindowActivator.Activate(hwnd);
        App.Log($"提前台：hwnd={hwnd} 成功={ok}", null);
    }
}
