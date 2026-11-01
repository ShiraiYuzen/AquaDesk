using System.Windows;
using System.Windows.Interop;
using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 三个浮层（Dock / 时间胶囊 / 状态胶囊）的 z 序调度。
///
/// 它们平时 `Topmost = true`，永远压在所有窗口上面。问题是窗口一最大化，
/// 浮层就骑在别人的内容上 —— 浏览器底部那条工具栏、编辑器最后一行代码都被吃掉，
/// 而最大化本身意思是「我要用满整个屏幕」。
///
/// 所以：**前台窗口最大化 / 全屏时，把浮层沉到 z 序最底**（`HWND_BOTTOM`），
/// 窗口还原成普通大小再抬回最前。判断由 Dock 那个 1.2 秒的轮询顺手做，
/// 多开一个计时器没意义。
///
/// 另外启动台 / 文件夹这类「临时置顶窗口」打开期间要按住浮层别抢置顶
/// （它们自己有 Topmost 让位逻辑），所以留了 <see cref="HoldTopmost"/> 这个钩子。
/// </summary>
public static class OverlayZOrder
{
    /// <summary>SetWindowPos 的 hWndInsertAfter：沉到最底。</summary>
    private static readonly IntPtr HwndBottom = new(1);

    private static readonly List<Window> Overlays = new();

    private static bool _sunk;

    /// <summary>有没有临时置顶窗口（启动台 / 文件夹）正开着；开着就别把浮层抢回置顶。</summary>
    public static Func<bool>? HoldTopmost { get; set; }

    /// <summary>现在是不是沉底状态（调试日志和测试用）。</summary>
    public static bool IsSunk => _sunk;

    public static void Register(Window window)
    {
        if (!Overlays.Contains(window)) Overlays.Add(window);
    }

    public static void Unregister(Window window) => Overlays.Remove(window);

    /// <summary>这个句柄是不是我们自己的浮层。窗口预览要跳过它们（不然 Dock 会预览自己）。</summary>
    public static bool IsOverlay(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        foreach (var window in Overlays)
        {
            if (new WindowInteropHelper(window).Handle == hwnd) return true;
        }

        return false;
    }

    /// <summary>按「前台窗口是否占满屏幕」设置沉底状态。状态没变就什么都不做。</summary>
    public static void SetSunk(bool sink)
    {
        if (sink == _sunk) return;

        _sunk = sink;
        App.Log($"浮层 z 序：前台窗口{(sink ? "最大化 / 全屏，沉到最底" : "恢复正常，抬回最前")}", null);

        Apply();
    }

    private static void Apply()
    {
        var held = HoldTopmost?.Invoke() == true;

        foreach (var window in Overlays.ToArray())
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) continue;

            if (_sunk)
            {
                // 先摘掉 Topmost，再显式沉底：光把 Topmost 设成 false 只会退到「普通窗口」那一层，
                // 仍然压在已经打开的普通窗口上面
                window.Topmost = false;
                NativeMethods.SetWindowPos(
                    hwnd, HwndBottom, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
            else if (!held)
            {
                window.Topmost = true;
                NativeMethods.SetWindowPos(
                    hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
            // held 且没沉底：置顶交给启动台 / 文件夹窗口自己管，这里不插手
        }
    }
}
