using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AquaDesk.Core;
using Microsoft.Win32;

namespace AquaDesk.Services;

/// <summary>当前键盘布局在菜单栏上的样子：优先图标，取不到就退回文字。</summary>
public sealed record InputMethodVisual(string Label, ImageSource? Icon);

/// <summary>
/// 某个语言下注册的一个 TSF 输入法。
/// <c>Clsid</c> + <c>Guid</c> 一起唯一确定它，激活时要用这对值。
/// </summary>
public sealed record TsfProfile(string Clsid, string Guid, string Description, string IconFile, int IconIndex)
{
    /// <summary>跨语言也唯一的记忆键（同一个 GUID 可能注册在多个语言下）。</summary>
    public string Key => Clsid + "/" + Guid;
}

/// <summary>
/// 读键盘布局 / 输入法的图标与名字。
///
/// 图标不在固定位置，而是每个输入法自己注册的：
/// <c>HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile\0x{语言ID}\{配置GUID}</c>
/// 下面有 <c>IconFile</c> 和 <c>IconIndex</c>。搜狗是
/// <c>C:\WINDOWS\system32\SogouTSF.ime</c> 的第 0 个图标，讯飞 / 微信 / 微软拼音
/// 各在自己的 dll 里 —— 所以**必须按 profile 逐个取**，不能按语言取一个就算。
///
/// 取不到的（纯键盘布局、或者某个输入法的图标资源坏了）用
/// <see cref="Generated"/> 现画一个：橙底白字的圆角方块，和搜狗那个同款手感。
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

            // 先问「这个语言下当前该显示哪个输入法」——面板里点过哪个就认哪个
            var profile = KeyboardLayouts.CurrentProfileHint(langId);
            var icon = profile is not null ? GetIcon(profile, monochrome: true) : null;

            if (icon is null) icon = GetIcon(langId, monochrome: true);
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

    /// <summary>取某个指定输入法的图标。</summary>
    public static ImageSource? GetIcon(TsfProfile profile, bool monochrome)
    {
        var key = $"{profile.Key}:{monochrome}";

        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var icon = profile.IconFile.Length > 0 && System.IO.File.Exists(profile.IconFile)
                ? Extract(profile.IconFile, profile.IconIndex, monochrome)
                : null;

            Cache[key] = icon;
            return icon;
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
            var icon = profile is null ? null : Extract(profile.IconFile, profile.IconIndex, monochrome);

            Cache[key] = icon;
            return icon;
        }
    }

    /// <summary>找这个语言下启用中的第一个 TSF 输入法（带图标的那个）。</summary>
    public static TsfProfile? FindProfile(ushort langId)
    {
        var profiles = FindProfiles(langId);
        return profiles.Count > 0 ? profiles[0] : null;
    }

    /// <summary>
    /// 找这个语言下**所有**启用中的 TSF 输入法。
    ///
    /// 老实现拿到第一个就 return，于是 0804 下永远只认搜狗 —— 讯飞 / 微信 / 微软拼音
    /// 明明都注册在这儿，只是排在后面。展开成列表才谈得上「切换」。
    /// </summary>
    public static List<TsfProfile> FindProfiles(ushort langId)
    {
        var list = new List<TsfProfile>();

        try
        {
            using var tip = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\CTF\TIP");
            if (tip is null) return list;

            // 顺序要和系统输入法列表一致：TIP 子键顺序 + profile 子键顺序，通常就是安装顺序
            foreach (var clsid in tip.GetSubKeyNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                // 子键名是 8 位十六进制：0x00000804，不是 0x0804
                using var language = tip.OpenSubKey($@"{clsid}\LanguageProfile\0x{langId:X8}");
                if (language is null) continue;

                foreach (var profileId in language.GetSubKeyNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                {
                    using var profile = language.OpenSubKey(profileId);
                    if (profile is null) continue;

                    // Enable 缺省视为启用；0 是「装了但关掉」
                    if (profile.GetValue("Enable") is int enabled && enabled == 0) continue;

                    var description = profile.GetValue("Description") as string ?? string.Empty;

                    var iconFile = profile.GetValue("IconFile") as string ?? string.Empty;
                    if (iconFile.Length > 0) iconFile = Environment.ExpandEnvironmentVariables(iconFile);

                    var index = profile.GetValue("IconIndex") is int value ? value : 0;

                    // 描述和图标都没有的，多半是系统内部占位项（TableTextService 之类），别列出来
                    if (description.Length == 0 && iconFile.Length == 0) continue;

                    list.Add(new TsfProfile(
                        clsid,
                        profileId,
                        description,
                        iconFile,
                        // Convert.ToInt32 会负溢出：图标索引偶尔是 0xFFFFFFEF 这种，转回 0
                        index < 0 ? 0 : index));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 读取输入法配置失败：{ex.Message}");
        }

        return list;
    }

    /// <summary>
    /// 现画一个图标：橙色圆角方块 + 白色首字，和搜狗输入法那个 logo 一个路数。
    /// 用于没有图标资源的项（纯键盘布局、讯飞/微信这种资源缺失时）。
    /// </summary>
    public static ImageSource Generated(ushort langId, string name, string badge)
    {
        var key = $"gen:{langId:X4}:{name}:{badge}";

        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var cached) && cached is not null) return cached;

            var bitmap = Render(FirstGlyph(name, badge));
            Cache[key] = bitmap;
            return bitmap;
        }
    }

    private static string FirstGlyph(string name, string badge)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var first = StringInfo.GetNextTextElement(name.Trim());

            if (first.Length > 0 && !char.IsWhiteSpace(first[0]) && !char.IsControl(first[0]))
            {
                return first.ToUpperInvariant();
            }
        }

        return badge.Length > 0 ? badge[..1].ToUpperInvariant() : "?";
    }

    private static ImageSource Render(string glyph)
    {
        const int size = 64;

        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            // 搜狗的橙：左上偏亮、右下偏红
            var brush = new LinearGradientBrush(
                Color.FromRgb(0xFF, 0xB3, 0x47),
                Color.FromRgb(0xF0, 0x60, 0x1E),
                45);

            dc.DrawRoundedRectangle(brush, null, new Rect(0, 0, size, size), 15, 15);

            // 顶部一层淡高光，压出「玻璃」感，和 Dock 上的磁贴底板呼应
            var gloss = new LinearGradientBrush(
                Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF),
                90);

            dc.DrawRoundedRectangle(gloss, null, new Rect(0, 0, size, size * 0.55), 15, 15);

            var typeface = new Typeface(
                new FontFamily("Microsoft YaHei UI"),
                FontStyles.Normal,
                FontWeights.Bold,
                FontStretches.Normal);

            var text = new FormattedText(
                glyph,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                size * 0.52,
                Brushes.White,
                1.0);

            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        return bitmap;
    }

    private static ImageSource? Extract(string iconFile, int iconIndex, bool monochrome)
    {
        var large = new IntPtr[1];
        var small = new IntPtr[1];

        if (NativeMethods.ExtractIconEx(iconFile, iconIndex, large, small, 1) == 0
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
