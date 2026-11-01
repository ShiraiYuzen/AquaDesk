using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 菜单栏输入法指示左边那个小标记：中 / 英 / A。
///
/// 规则（按用户要求）：
/// - **中文键盘**：大写锁定开着 → <c>A</c>（不管当前是中文还是英文输入）；
///   否则中文输入 → <c>中</c>，英文输入 → <c>英</c>。
/// - **英文键盘**：不显示（返回 null，由调用方把控件收起来）。
///
/// 两个判断分别靠：
/// - 大小写锁定：**挂到前台线程的输入队列上再读 <c>GetKeyState</c>**。
///   这里踩过坑：<c>GetAsyncKeyState</c> 在后台进程里恒定返回 false（实测连自己注入的按键都读不到），
///   而裸的 <c>GetKeyState</c> 只反映本线程消息队列里的状态——别的应用里按的 Caps Lock 我们收不到。
/// - 中文 / 英文输入：<c>ImmGetDefaultIMEWnd</c> + <c>WM_IME_CONTROL / IMC_GETCONVERSIONMODE</c>，
///   看 <c>IME_CMODE_NATIVE</c> 位。<c>ImmGetContext</c> 在这里没用，它只对调用线程自己的窗口有效。
/// </summary>
public static class InputState
{
    // 挂线程读键盘状态有一定开销，也不必 200ms 一次
    private static readonly TimeSpan CapsRefresh = TimeSpan.FromMilliseconds(400);

    private static bool _capsLock;
    private static DateTime _capsCheckedAt = DateTime.MinValue;

    /// <summary>要显示的标记；英文键盘返回 null 表示不显示。</summary>
    public static string? GetBadge()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            var layout = NativeMethods.GetKeyboardLayout(threadId);
            var langId = (ushort)(layout.ToInt64() & 0xFFFF);

            // 0x0804 简体中文，0x0404 繁体，0x0C04 香港
            var chinese = langId is 0x0804 or 0x0404 or 0x0C04;

            // 英文键盘不显示这个标记
            if (!chinese) return null;

            // 大写锁定优先级最高：中文键盘下不管中英文输入都显示 A
            if (IsCapsLockOn(threadId)) return "A";

            return IsNativeInput(hwnd) ? "中" : "英";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 判断输入状态失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>真实的大写锁定状态（带缓存，400ms 内复用）。</summary>
    private static bool IsCapsLockOn(uint foregroundThread)
    {
        if (DateTime.Now - _capsCheckedAt < CapsRefresh) return _capsLock;

        _capsCheckedAt = DateTime.Now;

        var currentThread = NativeMethods.GetCurrentThreadId();

        // 后台进程的 GetKeyState 只反映自己消息队列里的状态，
        // 挂到前台线程的输入队列上再读，拿到的才是真实的大写锁定
        var attached = foregroundThread != 0
                       && foregroundThread != currentThread
                       && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            _capsLock = (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }

        return _capsLock;
    }

    /// <summary>当前输入法是不是在中文（native）模式。拿不到就按中文算。</summary>
    private static bool IsNativeInput(IntPtr hwnd)
    {
        var imeWindow = NativeMethods.ImmGetDefaultIMEWnd(hwnd);
        if (imeWindow == IntPtr.Zero) return true;

        // 用带超时的版本：这是发给别的进程窗口的同步消息，
        // 对方卡住的话 SendMessage 会把我们的 UI 线程一起拖住。
        var ok = NativeMethods.SendMessageTimeoutW(
            imeWindow,
            NativeMethods.WM_IME_CONTROL,
            new IntPtr(NativeMethods.IMC_GETCONVERSIONMODE),
            IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG,
            120,
            out var result);

        if (ok == IntPtr.Zero) return true;

        return (result.ToInt64() & NativeMethods.IME_CMODE_NATIVE) != 0;
    }
}
