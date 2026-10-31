using System.Runtime.InteropServices;

namespace AquaDesk.Core;

/// <summary>
/// 只封装当前阶段真正用到的 Win32 调用。
/// 原则：不注入、不 Hook 系统 DLL，只用官方 API 改窗口样式。
/// </summary>
internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>带这个的窗口即使有 TOOLWINDOW 也算「应用窗口」（Alt+Tab 里能看到）。</summary>
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>鼠标穿透：让拖动浮层不吃点击，事件落到它下面的窗口上。</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    public const uint SHGFI_ICON = 0x00000100;
    public const uint SHGFI_LARGEICON = 0x00000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    /// <summary>让 Shell 自己解析 .lnk 并返回目标程序的图标。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>当前线程 / 指定线程正在用的键盘布局。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint threadId);

    /// <summary>从 exe / dll / ime 里按索引抽图标。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    /// <summary>把 "@%SystemRoot%\system32\input.dll,-5000" 这种间接字符串还原成本地化文本。</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int SHLoadIndirectString(string source, System.Text.StringBuilder buffer, int size, IntPtr reserved);

    public const int LOCALE_SLOCALIZEDDISPLAYNAME = 0x00000002;
    public const int LOCALE_SISO639LANGNAME = 0x00000059;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetLocaleInfoW(int locale, int lcType, System.Text.StringBuilder buffer, int size);

    /// <summary>加载 / 激活一个键盘布局，参数是 "00000804" 这种 8 位 KLID。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadKeyboardLayout(string pwszKlid, uint flags);

    public const uint KLF_ACTIVATE = 0x00000001;
    public const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

    // ---------------- 输入法中 / 英状态 ----------------

    public const int VK_CAPITAL = 0x14;

    /// <summary>
    /// 键盘切换状态（大写锁定等）。
    /// 只反映调用线程消息队列里的状态，所以要配合 <see cref="AttachThreadInput"/> 挂到前台线程上读。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vKey);

    public const uint WM_IME_CONTROL = 0x0283;
    public const int IMC_GETCONVERSIONMODE = 0x0001;
    public const int IME_CMODE_NATIVE = 0x0001;

    /// <summary>
    /// 取某个窗口默认的 IME 窗口。
    /// 不能直接用 <c>ImmGetContext</c>——它只对调用线程自己的窗口有效，
    /// 对别的进程的前台窗口一律返回 0。
    /// </summary>
    [DllImport("imm32.dll")]
    public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>带超时的 SendMessage：对方进程卡住时不会把调用方一起拖死。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    public const uint WM_SYSCOMMAND = 0x0112;

    // ---------------- 工作区 ----------------

    public const uint SPI_SETWORKAREA = 0x002F;
    public const uint SPI_GETWORKAREA = 0x0030;
    public const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref RECT data, uint winIni);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>系统菜单里的「关闭」，有些窗口不理 WM_CLOSE 但认这个。</summary>
    public const int SC_CLOSE = 0xF060;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static long GetExtendedStyle(IntPtr hWnd) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(hWnd, GWL_EXSTYLE);

    public static void SetExtendedStyle(IntPtr hWnd, long style)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, GWL_EXSTYLE, new IntPtr(style));
        }
        else
        {
            SetWindowLong32(hWnd, GWL_EXSTYLE, (int)style);
        }
    }

    // ---------------- 判断窗口是不是全屏 ----------------

    public const int GWL_STYLE = -16;

    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;

    public static long GetStyle(IntPtr hWnd) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64()
            : GetWindowLong32(hWnd, GWL_STYLE);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>
    /// 把当前进程的工作集还给系统。传 -1/-1 是「尽可能多回收」的约定写法，
    /// 下一次访问会重新缺页加载，所以只在真正空闲时才调。
    /// </summary>
    [DllImport("kernel32.dll")]
    public static extern bool SetProcessWorkingSetSize(
        IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    // ---------------- 窗口预览 ----------------

    /// <summary>PW_RENDERFULLCONTENT：让 PrintWindow 也能抓到 DWM 合成的窗口（UWP / WPF / 部分浏览器）。</summary>
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    public const uint GW_OWNER = 4;

    [DllImport("gdi32.dll")]
    public static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    public static extern bool StretchBlt(
        IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

    /// <summary>StretchBlt 的双线性/抖动模式，缩图不会出现锯齿块。</summary>
    public const int HALFTONE = 4;

    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ---------------- 进程信息（要能读提权进程） ----------------

    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, System.Text.StringBuilder exeName, ref int size);

    /// <summary>强杀进程。托盘程序没有窗口可以发 WM_CLOSE，只能这么退。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(
        IntPtr process,
        out FILETIME creationTime,
        out FILETIME exitTime,
        out FILETIME kernelTime,
        out FILETIME userTime);

    // ---------------- 抓屏（给玻璃折射用） ----------------

    public const int SRCCOPY = 0x00CC0020;

    /// <summary>把自己从屏幕捕获里排除掉，这样抓屏抓到的是「Dock 背后」而不是 Dock 自己。</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(
        IntPtr hdc, IntPtr hbm, uint start, uint lines,
        byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // ---------------- 窗口激活 / 最小化 ----------------

    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>SetForegroundWindow 被前台锁定挡住时的兜底，比 AttachThreadInput 那套简单。</summary>
    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    // ---------------- 解除前台锁 ----------------

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// 把键盘焦点交给某个窗口。**调用线程必须已经 AttachThreadInput 到目标线程**，
    /// 否则对别的线程的窗口无效。
    /// 只调 SetForegroundWindow 是不够的：窗口会被画到最前面，但键盘焦点还留在原来那，
    /// 用户点它一下才能输入 —— 就是「窗口在最上面却像在后台」的由来。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    /// <summary>查某个线程当前把键盘焦点放在哪个窗口上（跨线程验证激活是否真的生效）。</summary>
    public static IntPtr GetFocusedWindowOf(uint threadId)
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(threadId, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    /// <summary>显示但不激活：不会抢走前台焦点。</summary>
    public const int SW_SHOWNA = 8;

    // ---------------- 任务栏自动隐藏 ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    public const int ABM_SETSTATE = 10;
    public const int ABM_GETSTATE = 4;
    public const int ABS_AUTOHIDE = 0x0000001;
    public const int ABS_ALWAYSONTOP = 0x0000002;

    // ---- 把自己注册成 AppBar（占住屏幕底部，最大化窗口就不会被盖住） ----
    public const int ABM_NEW = 0;
    public const int ABM_REMOVE = 1;
    public const int ABM_QUERYPOS = 2;
    public const int ABM_SETPOS = 3;

    public const uint ABE_LEFT = 0;
    public const uint ABE_TOP = 1;
    public const uint ABE_RIGHT = 2;
    public const uint ABE_BOTTOM = 3;

    [DllImport("shell32.dll")]
    public static extern IntPtr SHAppBarMessage(int message, ref APPBARDATA data);

    public const uint WM_CLOSE = 0x0010;

    /// <summary>清空回收站。dwFlags 传 0 会走系统自带的确认框和进度条。</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOMOVE = 0x0002;

    /// <summary>SetWindowPos 的 hWndInsertAfter：置顶。</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }
}
