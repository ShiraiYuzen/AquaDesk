using System.Windows.Automation;
using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 顶部菜单栏那个托盘按钮：唤起 Windows 自己的托盘（「隐藏的图标」弹出层）。
///
/// Windows 11 把通知区域换成了 XAML 岛——<c>TrayNotifyWnd</c> 底下没有 <c>ToolbarWindow32</c>，
/// 溢出区是 <c>TopLevelWindowForOverflowXamlIsland</c>。所以「读 TBBUTTON / TRAYDATA 远程内存」
/// 那套经典做法完全失效，唯一可靠的入口是 UI Automation：托盘里的按钮都是
/// <c>SystemTray.NormalButton</c>，「隐藏的图标」那个箭头也是，直接 Invoke 它即可。
///
/// 这里刻意**不**自己画一份托盘网格。试过：隐藏图标是懒加载的，不开出溢出层 UIA 树里一个都没有，
/// 只能「短暂打开→读→关掉」，用户会看到右下角闪一下；而且读到了也没法让图标在别处弹自己的菜单
/// （拿不到 hWnd/uID）。与其做个半吊子的复制品，不如把系统原版调出来。
/// </summary>
public static class TrayService
{
    /// <summary>唤起系统托盘。</summary>
    public static bool OpenOverflow()
    {
        // 任务栏被「隐藏原生任务栏」藏起来时，UIA 连按钮都不返回（实测 Invoke 直接 false），
        // 所以先把它「显示但不激活」地亮一下；点完再把任务栏藏回去——
        // 弹出层是独立窗口，任务栏重新隐藏后它仍然留在屏幕上（实测确认）。
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);

        if (taskbar == IntPtr.Zero) return InvokeOverflowButton();

        var bounds = GetBounds(taskbar);

        // 任务栏处于「自动隐藏」时，Shell 会把它挪到屏幕下方（比如 0,1078），
        // 这时 IsWindowVisible 仍然返回 true —— 只按可见性判断会以为它好好的，
        // 结果按钮在屏幕外，UIA 一个都找不到。所以要连「跑到屏幕外」一起判。
        var parkedOffscreen = bounds is { } b && b.Top >= ScreenHeight() - 2;

        if (!parkedOffscreen && NativeMethods.IsWindowVisible(taskbar))
        {
            var direct = InvokeOverflowButton();
            App.Log($"托盘：任务栏本来就可见，Invoke={direct}", null);
            return direct;
        }

        var saved = bounds;

        if (parkedOffscreen && bounds is { } parked)
        {
            // 先把它挪回屏幕内（不显示），UIA 才认得出按钮
            MoveTo(taskbar, parked.Left, ScreenHeight() - (parked.Bottom - parked.Top));
        }

        // 自动隐藏状态下 SW_SHOWNA 只是把窗口「显示」出来，Shell 并不会去渲染它的内容，
        // UIA 树里依然是空的。必须正常 Show 一下，再等它真的画出来。
        NativeMethods.ShowWindow(taskbar, NativeMethods.SW_SHOW);
        Thread.Sleep(150);

        var opened = InvokeOverflowButton();

        if (opened) WaitForOverflow(600);

        NativeMethods.ShowWindow(taskbar, NativeMethods.SW_HIDE);

        if (parkedOffscreen && saved is { } back)
        {
            MoveTo(taskbar, back.Left, back.Top);
        }

        App.Log($"托盘：原本隐藏/停屏外={parkedOffscreen} 矩形={bounds} Invoke={opened}", null);
        return opened;
    }

    private static int ScreenHeight() =>
        (int)Math.Round(System.Windows.SystemParameters.PrimaryScreenHeight * DpiScale());

    private static double DpiScale()
    {
        var taskbar = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return 1.0;

        var dpi = NativeMethods.GetDpiForWindow(taskbar);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private static NativeMethods.RECT? GetBounds(IntPtr hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var rect) ? rect : null;

    private static void MoveTo(IntPtr hwnd, int left, int top)
    {
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private static void WaitForOverflow(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (FindOverflowWindow() is not null) return;

            Thread.Sleep(40);
        }
    }

    private static AutomationElement? FindOverflowWindow()
    {
        try
        {
            var window = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland"));

            return window is not null && !window.Current.IsOffscreen ? window : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool InvokeOverflowButton()
    {
        var button = FindOverflowButton();
        if (button is null)
        {
            App.Log("托盘：UIA 里找不到「隐藏的图标」按钮", null);
            return false;
        }

        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            App.Log("托盘：按钮不支持 Invoke 模式", null);
            return false;
        }

        try
        {
            ((InvokePattern)pattern).Invoke();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 打开托盘失败：{ex.Message}");
            return false;
        }
    }

    private static AutomationElement? FindOverflowButton()
    {
        var tray = FindTrayWindow();
        if (tray is null) return null;

        try
        {
            // 必须是 Descendants：这些按钮在 UIA 树上和 TrayNotifyWnd 是平级的，
            // 按 pane 往下找会一个都找不到。
            foreach (AutomationElement button in tray.FindAll(
                         TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                var name = CleanName(button.Current.Name);

                if (name.Contains("隐藏的图标", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("hidden icons", StringComparison.OrdinalIgnoreCase))
                {
                    return button;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 查找托盘入口失败：{ex.Message}");
        }

        return null;
    }

    private static AutomationElement? FindTrayWindow()
    {
        try
        {
            return AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 找不到任务栏：{ex.Message}");
            return null;
        }
    }

    /// <summary>气泡提示是多行的，取第一行。</summary>
    private static string CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        return raw.Split('\n', '\r')[0].Trim();
    }
}
