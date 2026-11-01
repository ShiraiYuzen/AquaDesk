using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AquaDesk.Core;
using Microsoft.Win32;

namespace AquaDesk.Services;

/// <summary>当前键盘布局在菜单栏上的样子：优先图标，取不到就退回文字。</summary>
public sealed record InputMethodVisual(string Label, ImageSource? Icon);

/// <summary>某个语言下注册的 TSF 输入法配置。</summary>
public sealed record TsfProfile(string Description, string IconFile, int IconIndex);

/// <summary>
/// 读键盘布局 / 输入法的图标与名字。
///
/// 图标不在固定位置，而是每个输入法自己注册的：
/// <c>HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile\0x{语言ID}\{配置GUID}</c>
/// 下面有 <c>IconFile</c> 和 <c>IconIndex</c>。搜狗就是
/// <c>C:\WINDOWS\system32\SogouTSF.ime</c> 的第 0 个图标。
///
/// 菜单栏是深色的，所以栏上那份要转成单色白：按亮度做掩膜，把底板压掉、只留标记。
/// 下拉框里则用原色，跟 Windows 自己的键盘列表一致。
/// </summary>
public static class InputMethodIcon
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    private static readonly int[] ChineseLanguages = { 0x0804, 0x0404, 0x0C04 };

    /// <summary>查当前前台窗口正在用的键盘布局。</summary>
    public static InputMethodVisual GetCurrent()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            var layout = NativeMethods.GetKeyboardLayout(threadId);
            var langId = (ushort)(layout.ToInt64() & 0xFFFF);

            var icon = GetIcon(langId, monochrome: true);
            if (icon is not null) return new InputMethodVisual(string.Empty, icon);

            // 没有注册图标的（比如美式键盘）就用文字
            return new InputMethodVisual(ChineseLanguages.Contains(langId) ? "中" : "EN", null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 取输入法图标失败：{ex.Message}");
            return new InputMethodVisual("EN", null);
        }
    }

    /// <summary>取某个语言的输入法图标。<paramref name="monochrome"/> 为 true 时转成单色白。</summary>
    public static ImageSource? GetIcon(ushort langId, bool monochrome)
    {
        var key = $"{langId:X4}:{monochrome}";

        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var profile = FindProfile(langId);
            var icon = profile is null ? null : Extract(profile, monochrome);

            Cache[key] = icon;
            return icon;
        }
    }

    /// <summary>找这个语言下启用中的 TSF 输入法配置（带图标的那个）。</summary>
    public static TsfProfile? FindProfile(ushort langId)
    {
        try
        {
            using var tip = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\CTF\TIP");
            if (tip is null) return null;

            foreach (var clsid in tip.GetSubKeyNames())
            {
                // 子键名是 8 位十六进制：0x00000804，不是 0x0804
                using var language = tip.OpenSubKey($@"{clsid}\LanguageProfile\0x{langId:X8}");
                if (language is null) continue;

                foreach (var profileId in language.GetSubKeyNames())
                {
                    using var profile = language.OpenSubKey(profileId);
                    if (profile is null) continue;

                    // Enable 缺省视为启用；0 是「装了但关掉」
                    if (profile.GetValue("Enable") is int enabled && enabled == 0) continue;

                    if (profile.GetValue("IconFile") is not string iconFile) continue;

                    var path = Environment.ExpandEnvironmentVariables(iconFile);
                    if (!System.IO.File.Exists(path)) continue;

                    var description = profile.GetValue("Description") as string ?? string.Empty;
                    var index = profile.GetValue("IconIndex") is int value ? value : 0;

                    return new TsfProfile(description, path, index);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 读取输入法配置失败：{ex.Message}");
        }

        return null;
    }

    private static ImageSource? Extract(TsfProfile profile, bool monochrome)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];

        if (NativeMethods.ExtractIconEx(profile.IconFile, profile.IconIndex, large, small, 1) == 0
            || large[0] == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            if (!monochrome)
            {
                source.Freeze();
                return source;
            }

            return ToWhite(source);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) NativeMethods.DestroyIcon(large[0]);
            if (small[0] != IntPtr.Zero) NativeMethods.DestroyIcon(small[0]);
        }
    }

    /// <summary>
    /// 彩色图标 → 白色单色图标。
    ///
    /// 做法是把亮度当遮罩：亮的（标记）保留，暗的（底板）压掉。
    /// 但有些图标是「亮底暗字」，直接这么转会变成一坨实心白块，
    /// 所以先算一遍覆盖率，太满就把掩膜反过来。
    /// </summary>
    private static ImageSource ToWhite(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;

        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        var count = width * height;
        var mask = new double[count];

        var opaque = 0;
        var coverage = 0.0;

        for (var p = 0; p < count; p++)
        {
            var i = p * 4;
            if (pixels[i + 3] < 24) continue;

            opaque++;
            var m = SmoothStep(0.62, 0.92, Luminance(pixels, i));
            mask[p] = m;
            coverage += m;
        }

        if (opaque == 0) return source;

        if (coverage / opaque > 0.55)
        {
            coverage = 0;

            for (var p = 0; p < count; p++)
            {
                var i = p * 4;
                if (pixels[i + 3] < 24) continue;

                var m = SmoothStep(0.62, 0.92, 1 - Luminance(pixels, i));
                mask[p] = m;
                coverage += m;
            }
        }

        // 整体都是中间调，掩膜会几乎全空——退回「不透明即白」，至少不会变成一片空白
        if (coverage / opaque < 0.05)
        {
            for (var p = 0; p < count; p++)
            {
                mask[p] = pixels[p * 4 + 3] >= 24 ? 1 : 0;
            }
        }

        var output = new byte[pixels.Length];

        for (var p = 0; p < count; p++)
        {
            var i = p * 4;

            output[i] = 255;
            output[i + 1] = 255;
            output[i + 2] = 255;
            output[i + 3] = (byte)Math.Round(pixels[i + 3] * mask[p]);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, output, stride);
        bitmap.Freeze();

        return bitmap;
    }

    private static double Luminance(byte[] bgra, int index) =>
        (0.114 * bgra[index] + 0.587 * bgra[index + 1] + 0.299 * bgra[index + 2]) / 255.0;

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
