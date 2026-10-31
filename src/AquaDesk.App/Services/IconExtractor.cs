using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 从 Shell 取高清图标。
///
/// 走 <c>IShellItemImageFactory</c> 而不是 SHGetFileInfo：
/// - SHGetFileInfo 最大只给 32x32（系统小图标），放到 52px 的 Dock 上糊成一片；
/// - SHGetFileInfo 的 HICON 经 CreateBitmapSourceFromHIcon 转换后经常丢 alpha，图标带白底；
/// - 快捷方式还会被叠上左下角的蓝色小箭头。
///
/// IShellItemImageFactory 直接返回指定尺寸的 32bpp ARGB 位图，干净、无叠加标记。
/// </summary>
public static class IconExtractor
{
    private const int RequestedPx = 128;

    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private const int SIIGBF_ICONONLY = 0x04;

    public static ImageSource? FromShortcut(string shortcutPath)
    {
        // 优先用 lnk 指向的真实 exe 取图标。
        // 直接喂 .lnk 时 Shell 有时候会给「通用文件」图标（一张白纸），
        // 尤其是快捷方式带自定义图标索引、或者指向 UWP 包的时候。
        var target = ShortcutResolver.ResolveTargetPath(shortcutPath);

        if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
        {
            var fromTarget = TryShellImageFactory(target);
            if (fromTarget is not null) return fromTarget;
        }

        var image = TryShellImageFactory(shortcutPath);
        return image ?? TryLegacyIcon(shortcutPath);
    }

    /// <summary>按 exe 路径取图标（后台程序面板用，路径来自进程表时一定是真实存在的）。</summary>
    public static ImageSource? FromExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        return TryShellImageFactory(path) ?? TryLegacyIcon(path);
    }

    /// <summary>
    /// 按 AppUserModelID 取应用图标（媒体会话给的就是这个）。
    ///
    /// 它有三种形态，得分别对付：
    /// - 完整 exe 路径（部分播放器这么注册）
    /// - 只有 exe 文件名，比如 <c>Spotify.exe</c> / <c>QQMusic.exe</c>
    /// - 打包应用，形如 <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c>
    ///
    /// 前两种去进程表里找真实路径（最准，也顺手支持了绿色版）；
    /// 第三种走 <c>shell:AppsFolder\&lt;AUMID&gt;</c>，Shell 会解析出包里的 Logo。
    /// </summary>
    public static ImageSource? FromAppUserModelId(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;

        if (appId.Contains('\\') || appId.Contains('/'))
        {
            if (File.Exists(appId)) return TryShellImageFactory(appId);
        }
        else if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var fromProcess = TryFromProcessName(Path.GetFileNameWithoutExtension(appId));
            if (fromProcess is not null) return fromProcess;
        }

        var fromShell = TryShellImageFactory($@"shell:AppsFolder\{appId}");
        if (fromShell is not null) return fromShell;

        return TryFromProcessName(Path.GetFileNameWithoutExtension(appId));
    }

    /// <summary>拿正在运行的进程的可执行文件路径去取图标；拿不到就返回 null。</summary>
    private static ImageSource? TryFromProcessName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

                    var image = TryShellImageFactory(path);
                    if (image is not null) return image;
                }
                catch
                {
                    // 对方是管理员权限的进程时读不了 MainModule，跳过
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // 枚举进程失败就算了，退回 null 让界面画音符
        }

        return null;
    }

    private static ImageSource? TryShellImageFactory(string path)
    {
        object? shellItem = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            Shell32.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out shellItem);

            if (shellItem is not IShellItemImageFactory factory) return null;

            var size = new SIZE { cx = RequestedPx, cy = RequestedPx };
            var hr = factory.GetImage(size, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                Gdi32.DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] IShellItemImageFactory 失败 {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (shellItem is not null && Marshal.IsComObject(shellItem))
            {
                Marshal.ReleaseComObject(shellItem);
            }
        }
    }

    /// <summary>兜底：老 API，只有 32x32，且可能带白底。</summary>
    private static ImageSource? TryLegacyIcon(string path)
    {
        var info = new NativeMethods.SHFILEINFO();
        var ok = NativeMethods.SHGetFileInfo(
            path, 0, ref info,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);

        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    // ---------------- COM / Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    private static class Shell32
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    private static class Gdi32
    {
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);
    }
}
