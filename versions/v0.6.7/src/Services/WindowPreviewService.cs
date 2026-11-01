using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>一个可以展示的顶层窗口。</summary>
public sealed record OpenWindow(IntPtr Handle, string Title, string ExePath, DateTime StartedAt);

/// <summary>
/// 枚举当前打开的窗口，并把它们抓成缩略图。
///
/// 抓图用 <c>PrintWindow(PW_RENDERFULLCONTENT)</c> 而不是 DWM 实时缩略图：
/// DWM 缩略图要挂在一个独立子窗口上，而 Dock 是 AllowsTransparency 的透明窗口，
/// 子窗口会被画成一块不透明方块，圆角也没了。静态抓图可以直接贴进 WPF 的 ImageBrush。
/// </summary>
public static class WindowPreviewService
{
    /// <summary>
    /// 预览区硬上限。可见格数按 Dock 可用宽度动态算（见 DockViewModel.PreviewSlotLimit），
    /// 但再宽也不超过这个数 —— 一屏同时抓十几张缩略图，PrintWindow 的开销顶不住。
    /// </summary>
    public const int MaxPreviewSlots = 12;

    /// <summary>还没算过可用宽度时的默认格数（首次布局前用）。</summary>
    public const int DefaultPreviewSlots = 6;

    /// <summary>枚举时最多收这么多个候选，排序后由预览区分组取用。</summary>
    private const int MaxScan = 60;

    /// <summary>
    /// 抓不出来的窗口记在这里，别每 2 秒重试一次 —— 日志会被刷爆，CPU 也白烧。
    /// 窗口集合变化（有窗口开关）时清空，给它们一次翻身机会。
    /// </summary>
    private static readonly HashSet<IntPtr> Unsupported = new();

    public static void ResetFailureCache() => Unsupported.Clear();

    /// <summary>
    /// 每个窗口第一次被看到时的序号。用来给「同一个进程的多个窗口」定先后 ——
    /// 只认进程启动时间的话，同一进程的几个窗口会分不出谁先开。
    /// </summary>
    private static readonly Dictionary<IntPtr, long> FirstSeen = new();

    private static long _seenCounter;

    public static List<OpenWindow> Enumerate()
    {
        var found = new List<OpenWindow>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            // EnumWindows 给的是 Z 序（最近用过的在前），而且窗口一调前台顺序就变。
            // 所以先尽量多收，排序完再截断。
            if (found.Count >= MaxScan) return false;

            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            // 有 owner 的是对话框 / 工具窗，不算独立窗口
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;

            if ((NativeMethods.GetExtendedStyle(hwnd) & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            // UWP 会留下一堆看不见的「幽灵窗口」，靠 DWM 的 cloaked 标记排掉
            if (NativeMethods.DwmGetWindowAttribute(
                    hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
                && cloaked != 0)
            {
                return true;
            }

            var length = NativeMethods.GetWindowTextLengthW(hwnd);
            if (length <= 0) return true;

            var buffer = new System.Text.StringBuilder(length + 2);
            if (NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity) == 0) return true;

            var title = buffer.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);

            // 自己的浮层（Dock / 两个胶囊 / 启动台）不能列 —— 否则 Dock 会预览它自己。
            // 但设置窗口这类「正常窗口」要列出来：用户开着设置页时，那本来就是桌面上多出来的一个窗口。
            // （0.4.5 之前这里按 pid 一刀切，设置窗口永远不出现在预览条里）
            if (OverlayZOrder.IsOverlay(hwnd)) return true;

            if (!FirstSeen.ContainsKey(hwnd)) FirstSeen[hwnd] = ++_seenCounter;

            var info = GetProcessInfo((int)processId);

            found.Add(new OpenWindow(hwnd, title, info.ExePath, info.StartedAt));

            return true;
        }, IntPtr.Zero);

        // 按「软件启动的先后」排。同进程开多个窗口时，用第一次被看到的顺序兜底。
        // 之后把哪个窗口调到前台都不会改变这个顺序 —— 排序只跟启动时间有关。
        // 不在这里截断：上层会按「应用」合并窗口，截断要等合并完再做（DockViewModel）。
        return found
            .OrderBy(w => w.StartedAt)
            .ThenBy(w => FirstSeen.TryGetValue(w.Handle, out var seq) ? seq : long.MaxValue)
            .ToList();
    }

    private readonly record struct ProcessInfo(string ExePath, DateTime StartedAt);

    /// <summary>进程的 exe 路径和启动时间缓存。</summary>
    private static readonly Dictionary<int, ProcessInfo> ProcessInfos = new();

    /// <summary>
    /// 用句柄 API 而不是 <c>Process.StartTime</c>：后者对**提权进程**（任务管理器就是）
    /// 会直接抛异常，拿不到启动时间也拿不到 exe 路径。
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> 对同用户的提权进程是允许的。
    /// </summary>
    private static ProcessInfo GetProcessInfo(int processId)
    {
        lock (ProcessInfos)
        {
            if (ProcessInfos.TryGetValue(processId, out var cached)) return cached;
        }

        var info = new ProcessInfo(string.Empty, DateTime.MaxValue);

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);

        if (handle != IntPtr.Zero)
        {
            try
            {
                var buffer = new System.Text.StringBuilder(1024);
                var size = buffer.Capacity;

                if (NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                {
                    info = info with { ExePath = buffer.ToString() };
                }

                if (NativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _))
                {
                    var fileTime = ((long)created.dwHighDateTime << 32) | created.dwLowDateTime;
                    if (fileTime > 0) info = info with { StartedAt = DateTime.FromFileTime(fileTime) };
                }
            }
            catch
            {
                // 读不到就保持默认值，排序时落到最后
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        lock (ProcessInfos)
        {
            if (ProcessInfos.Count > 500) ProcessInfos.Clear();
            ProcessInfos[processId] = info;
        }

        return info;
    }

    /// <summary>
    /// 已知的密码 / 密钥类应用（exe 名，不带扩展名，小写比较）。
    /// 这些应用大多没设 WDA 保护，但内容一旦被抓进常驻浮层就等于公开 —— 直接按名字兜住。
    /// </summary>
    private static readonly string[] SensitiveProcesses =
    {
        "1password", "1passwordbrowser", "bitwarden", "keepass", "keepassxc",
        "lastpass", "dashlane", "enpass", "nordpass", "roboform",
        "keeper", "keeperpasswordmanager", "protonpass", "bitwarden-desktop",
        "authy", "authy desktop", "winauth", "strongbox"
    };

    /// <summary>
    /// 这个窗口的缩略图该不该抓。两道判断：
    /// 1. 窗口自己声明了捕获保护（<c>WDA_MONITOR</c> / <c>WDA_EXCLUDEFROMCAPTURE</c>）——
    ///    密码管理器、DRM 播放器普遍这么干，本该被系统级尊重；
    /// 2. 没声明但属于已知密码 / 密钥类应用（按 exe 名）。
    /// </summary>
    public static bool IsCaptureForbidden(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;

        try
        {
            if (NativeMethods.GetWindowDisplayAffinity(hwnd, out var affinity)
                && affinity != NativeMethods.WDA_NONE)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0) return false;

            var exePath = GetProcessInfo((int)processId).ExePath;
            if (string.IsNullOrEmpty(exePath)) return false;

            var name = System.IO.Path.GetFileNameWithoutExtension(exePath);
            return SensitiveProcesses.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // 查不了就当不限制：宁可显示缩略图，也别因为一次异常把预览整片弄空
            return false;
        }
    }

    /// <summary>抓一张缩略图。窗口最小化、抓不到、或**不该抓**时返回 null，界面会退回应用图标。</summary>
    public static ImageSource? Capture(IntPtr hwnd, int width, int height)
    {
        if (hwnd == IntPtr.Zero || width <= 0 || height <= 0) return null;

        // 隐私边界：窗口自己声明「禁止捕获」的、以及密码 / 密钥类应用，一律不抓。
        // 这类内容一旦进缩略图，就等于暴露在任何截屏、录屏和旁人视线里。
        if (IsCaptureForbidden(hwnd)) return null;

        if (NativeMethods.IsIconic(hwnd)) return null;
        if (Unsupported.Contains(hwnd)) return null;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return null;

        var sourceWidth = rect.Right - rect.Left;
        var sourceHeight = rect.Bottom - rect.Top;

        // 尺寸离谱的直接放弃，别去申请几十 MB 的位图
        if (sourceWidth < 8 || sourceHeight < 8) return null;
        if (sourceWidth > 8000 || sourceHeight > 8000) return null;

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var fullDc = IntPtr.Zero;
        var fullBitmap = IntPtr.Zero;
        var smallDc = IntPtr.Zero;
        var smallBitmap = IntPtr.Zero;
        var oldFull = IntPtr.Zero;
        var oldSmall = IntPtr.Zero;

        try
        {
            fullDc = NativeMethods.CreateCompatibleDC(screenDc);
            fullBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, sourceWidth, sourceHeight);
            if (fullDc == IntPtr.Zero || fullBitmap == IntPtr.Zero) return null;

            oldFull = NativeMethods.SelectObject(fullDc, fullBitmap);

            // PW_RENDERFULLCONTENT 能抓到 DWM 合成的窗口（UWP / WPF），但 Chromium 系（Electron、Chrome）
            // 常常直接返回失败。失败就退回老的 WM_PRINT 路径再试一次。
            if (!NativeMethods.PrintWindow(hwnd, fullDc, NativeMethods.PW_RENDERFULLCONTENT)
                && !NativeMethods.PrintWindow(hwnd, fullDc, 0))
            {
                Unsupported.Add(hwnd);
                App.Log($"窗口预览：PrintWindow 失败，已跳过 hwnd={hwnd} size={sourceWidth}x{sourceHeight}", null);
                return null;
            }

            smallDc = NativeMethods.CreateCompatibleDC(screenDc);
            smallBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (smallDc == IntPtr.Zero || smallBitmap == IntPtr.Zero) return null;

            oldSmall = NativeMethods.SelectObject(smallDc, smallBitmap);

            // 缩放放在 GDI 里做：托管侧只拿回 width×height 这一小块，不会为了 1080p 窗口
            // 每次分配 8MB 的 byte[]。
            NativeMethods.SetStretchBltMode(smallDc, NativeMethods.HALFTONE);
            if (!NativeMethods.StretchBlt(
                    smallDc, 0, 0, width, height,
                    fullDc, 0, 0, sourceWidth, sourceHeight,
                    NativeMethods.SRCCOPY))
            {
                return null;
            }

            var pixels = new byte[width * height * 4];

            var info = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            if (NativeMethods.GetDIBits(smallDc, smallBitmap, 0, (uint)height, pixels, ref info, 0) == 0)
            {
                Unsupported.Add(hwnd);
                App.Log($"窗口预览：GetDIBits 失败，已跳过 hwnd={hwnd}", null);
                return null;
            }

            // 必须用 Bgr32 而不是 Bgra32：
            // GDI 画东西不写 alpha 通道，CreateCompatibleBitmap 出来的位图 alpha 全是 0，
            // 按 Bgra32 解析就是一张全透明的图（界面上看着就是空白）。
            // Bgr32 直接忽略 alpha 字节，按不透明处理。
            var source = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);

            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 抓窗口缩略图失败 {hwnd}: {ex.Message}");
            return null;
        }
        finally
        {
            if (oldFull != IntPtr.Zero) NativeMethods.SelectObject(fullDc, oldFull);
            if (fullBitmap != IntPtr.Zero) NativeMethods.DeleteObject(fullBitmap);
            if (fullDc != IntPtr.Zero) NativeMethods.DeleteDC(fullDc);

            if (oldSmall != IntPtr.Zero) NativeMethods.SelectObject(smallDc, oldSmall);
            if (smallBitmap != IntPtr.Zero) NativeMethods.DeleteObject(smallBitmap);
            if (smallDc != IntPtr.Zero) NativeMethods.DeleteDC(smallDc);

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把窗口提到前台；最小化的先还原。</summary>
    public static void Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }

            BringToForeground(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 激活窗口失败 {hwnd}: {ex.Message}");
        }
    }

    /// <summary>
    /// 点预览的三态，跟任务栏一致：
    /// 已最小化 → 还原并切到前台；已经在前台 → 最小化；在后台 → 切到前台。
    /// </summary>
    public static void Toggle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                BringToForeground(hwnd);
                return;
            }

            // 点 Dock 不会改变前台窗口（NOACTIVATE），所以这里拿到的就是「点之前谁在前台」
            if (NativeMethods.GetForegroundWindow() == hwnd)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
                return;
            }

            BringToForeground(hwnd);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 切换窗口失败 {hwnd}: {ex.Message}");
        }
    }

    private static void BringToForeground(IntPtr hwnd) => WindowActivator.Activate(hwnd);

    /// <summary>
    /// 能不能对这个窗口动手（抓图 / 发关闭消息）。
    ///
    /// 任务管理器这类**提权进程**的窗口，UIPI 会拦掉低权限进程的 PrintWindow 和 WM_CLOSE，
    /// 而且不报错——表现就是「抓出来是空白」「点了关闭没反应」。
    /// 判断办法：试着拿 PROCESS_TERMINATE 权限，拿不到就是提权的。
    /// </summary>
    public static bool CanControl(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0) return false;

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_TERMINATE, false, processId);
        if (handle == IntPtr.Zero) return false;

        NativeMethods.CloseHandle(handle);
        return true;
    }

    /// <summary>
    /// 关窗口。用 SendMessageTimeout 而不是 PostMessage —— 后者被 UIPI 拦下时
    /// 返回值照样是成功，根本不知道消息被丢了。
    /// </summary>
    public static bool Close(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            // 标准路径：WM_CLOSE
            var closed = NativeMethods.SendMessageTimeoutW(
                hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero;

            if (closed) return true;

            var closeError = Marshal.GetLastWin32Error();

            // 有些窗口（任务管理器这类 XAML 岛的）不理 WM_CLOSE，改走系统菜单的「关闭」
            var viaMenu = NativeMethods.SendMessageTimeoutW(
                hwnd, NativeMethods.WM_SYSCOMMAND, new IntPtr(NativeMethods.SC_CLOSE), IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero;

            if (!viaMenu)
            {
                App.Log($"窗口预览：关闭失败 hwnd={hwnd} WM_CLOSE错误码={closeError} " +
                        $"SC_CLOSE错误码={Marshal.GetLastWin32Error()}（5=权限不足/被UIPI拦）", null);
            }

            return viaMenu;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 关闭窗口失败 {hwnd}: {ex.Message}");
            return false;
        }
    }
}
