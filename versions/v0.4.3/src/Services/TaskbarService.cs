using System.Runtime.InteropServices;
using System.Windows.Threading;
using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 隐藏 / 显示原生任务栏，并把它占的那一条从工作区里释放出来。
///
/// 踩过的坑，记下来免得再试：
/// 1. <c>ShowWindow(SW_HIDE)</c> 只是把窗口藏了，**工作区那一条不会还回来**，
///    最大化窗口底部永远差 48px。
/// 2. <c>SPI_SETWORKAREA</c> 想手动改回去：返回 True，但 Shell 立刻覆盖回原值 ——
///    任务栏自己是 AppBar，工作区由它说了算，别人抢不走（实测 26~1080 → 立刻变回 0~1032）。
/// 3. 唯一有效的办法是 <c>ABM_SETSTATE + ABS_AUTOHIDE</c>：任务栏切成自动隐藏后，
///    Shell 会重算工作区，48px 就回来了。
///
/// 但自动隐藏有个副作用：鼠标一碰屏幕底边，Shell 就把任务栏拉出来。
/// 所以配一个 120ms 的守卫定时器，看见它露头就按回去。
/// </summary>
public static class TaskbarService
{
    private static DispatcherTimer? _guard;

    public static bool IsVisible()
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        return taskbar != IntPtr.Zero && NativeMethods.IsWindowVisible(taskbar);
    }

    public static bool SetVisible(bool visible)
    {
        try
        {
            var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return false;

            if (visible)
            {
                StopGuard();
                NativeMethods.ShowWindow(taskbar, NativeMethods.SW_SHOW);
                SetState(taskbar, NativeMethods.ABS_ALWAYSONTOP);
            }
            else
            {
                // 顺序很重要：先切成自动隐藏让 Shell 释放工作区，再藏窗口，最后开守卫
                SetState(taskbar, NativeMethods.ABS_AUTOHIDE);
                NativeMethods.ShowWindow(taskbar, NativeMethods.SW_HIDE);
                StartGuard();
            }

            App.Log($"任务栏：visible={visible} 自动隐藏={IsAutoHide(taskbar)}", null);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 切换任务栏可见性失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 自动隐藏的任务栏，鼠标碰到屏幕底边就会被 Shell 拉出来。
    /// 这个定时器盯着它，一露头就按回去。
    /// </summary>
    private static void StartGuard()
    {
        if (_guard is not null) return;

        _guard = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };

        _guard.Tick += (_, _) =>
        {
            var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return;

            if (NativeMethods.IsWindowVisible(taskbar))
            {
                NativeMethods.ShowWindow(taskbar, NativeMethods.SW_HIDE);
            }
        };

        _guard.Start();
    }

    private static void StopGuard()
    {
        _guard?.Stop();
        _guard = null;
    }

    private static void SetState(IntPtr taskbar, int state)
    {
        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = taskbar,
            lParam = new IntPtr(state)
        };

        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref data);
    }

    private static bool IsAutoHide(IntPtr taskbar)
    {
        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = taskbar
        };

        var state = (long)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref data);
        return (state & NativeMethods.ABS_AUTOHIDE) != 0;
    }
}
