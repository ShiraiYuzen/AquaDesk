using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 焦点守卫：我们不是前台，就不该握着键盘焦点。
///
/// 背景：Dock / 状态胶囊 / 面板都是 NOACTIVATE 浮层，正常情况下点它们不会改变前台。
/// 但 WPF 在鼠标按下时仍可能把键盘焦点设到自己的窗口上 —— 这时前台还是原来的应用，
/// 焦点却被我们拿走了。用户回到应用里打字没反应，而且因为应用已经在前台，
/// Windows 不会重新发激活消息，点它也不管用，只能靠 Dock 图标切一次。
///
/// 这里每 500ms 检查一次：**前台是别的进程、而我们的 UI 线程还攥着焦点** → 主动放开。
/// 正常情况下我们当前台时不会触发，代价就是一次 GetForegroundWindow。
/// </summary>
public static class FocusGuard
{
    private static uint _ownPid;

    public static void Start(uint ownPid) => _ownPid = ownPid;

    public static void Tick()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;

        NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (foregroundPid == _ownPid) return;   // 前台是我们，焦点在我们身上天经地义

        var thread = NativeMethods.GetCurrentThreadId();
        if (NativeMethods.GetFocusedWindowOf(thread) == IntPtr.Zero) return;   // 我们本来就没焦点

        // 不是前台却握着焦点：放掉，让前台应用自己恢复
        NativeMethods.SetFocus(IntPtr.Zero);
        App.Log("焦点守卫：前台不是我们，主动释放错位的键盘焦点", null);
    }
}
