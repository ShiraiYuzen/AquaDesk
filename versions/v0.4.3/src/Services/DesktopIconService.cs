using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 隐藏 / 显示桌面图标。
/// 桌面图标其实是 Progman（或 WorkerW）下面 SHELLDLL_DefView 里的一个 SysListView32，
/// 直接 ShowWindow 那个列表控件就行，文件本身不受影响。
/// </summary>
public static class DesktopIconService
{
    public static bool SetVisible(bool visible)
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero) return false;

        NativeMethods.ShowWindow(listView, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        return true;
    }

    public static bool IsVisible()
    {
        var listView = FindDesktopListView();
        return listView != IntPtr.Zero && NativeMethods.IsWindowVisible(listView);
    }

    private static IntPtr FindDesktopListView()
    {
        // 常规路径：Progman → SHELLDLL_DefView → SysListView32
        var progman = NativeMethods.FindWindowW("Progman", null);
        var defView = NativeMethods.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        // 某些系统（开了「显示桌面图标」以外的壁纸插件）会把 DefView 挪到某个 WorkerW 下
        if (defView == IntPtr.Zero)
        {
            IntPtr found = IntPtr.Zero;

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                var child = NativeMethods.FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (child == IntPtr.Zero) return true;

                found = child;
                return false;
            }, IntPtr.Zero);

            defView = found;
        }

        return defView == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.FindWindowExW(defView, IntPtr.Zero, "SysListView32", null);
    }
}
