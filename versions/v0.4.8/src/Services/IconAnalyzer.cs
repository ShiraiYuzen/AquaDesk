using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AquaDesk.Services;

/// <summary>图标怎么铺满磁贴。</summary>
public enum IconLayout
{
    /// <summary>铺满：图标裁成圆角方形，必要时垫一层同色底板把透明处补上。</summary>
    Fill,

    /// <summary>内嵌：透明底的字形图标，垫对比色底板后把图标缩进一圈放上去。</summary>
    Inset
}

/// <summary>
/// 分析图标，决定它该怎么铺成「方形圆角」。
///
/// 只看「不透明像素占比」不够用：正圆形的占比是 π/4 ≈ 78%，会被误判成「整块画」，
/// 结果头像那种图标裁完还是个圆，四个角漏出 Dock 底色。
///
/// 所以改看**边缘填充率**——沿四条边各取一条内缩 5% 的线，量这条线上不透明像素的比例：
/// - 圆角方形 / 整块方图：边缘连续，≈ 0.9
/// - 正圆：边缘只有中间一段弧，≈ 0.45
///
/// 判定顺序：
/// 1. 边缘填充率高 → 整块画，直接裁圆角，不加底板（不会出现白边）；
/// 2. 边缘不连续但有明显主色 → 主色就是它的背景色，用它铺满，图标原尺寸叠上去，接缝看不出来；
/// 3. 剩下的是细字形图标 → 取对比色当底板，图标内嵌一圈。
/// </summary>
public static class IconAnalyzer
{
    /// <summary>边缘填充率高于此值视为整块画。</summary>
    private const double EdgeFillThreshold = 0.62;

    /// <summary>主色占比高于此值，认为主色就是图标的背景色。</summary>
    private const double DominantShareThreshold = 0.28;

    public static (IconLayout Layout, Color PlateColor) Analyze(ImageSource source)
    {
        try
        {
            if (source is not BitmapSource bitmap) return (IconLayout.Fill, Colors.Transparent);

            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width <= 0 || height <= 0) return (IconLayout.Fill, Colors.Transparent);

            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var edgeFill = MeasureEdgeFill(pixels, width, height);
            if (edgeFill >= EdgeFillThreshold) return (IconLayout.Fill, Colors.Transparent);

            var (dominant, share) = DominantColor(pixels, width, height);

            if (share >= DominantShareThreshold)
            {
                return (IconLayout.Fill, Color.FromRgb(dominant.R, dominant.G, dominant.B));
            }

            var average = AverageColor(pixels, width, height);
            return (IconLayout.Inset, BuildContrastPlate(average));
        }
        catch
        {
            // 分析失败就退回「整块裁切」，至少不会出现白底
            return (IconLayout.Fill, Colors.Transparent);
        }
    }

    // ---------------- 边缘填充率 ----------------

    private static double MeasureEdgeFill(byte[] pixels, int width, int height)
    {
        var inset = Math.Max(1, (int)(Math.Min(width, height) * 0.05));
        var total = 0.0;
        var count = 0;

        for (var x = inset; x < width - inset; x++)
        {
            total += Alpha(pixels, width, x, inset) + Alpha(pixels, width, x, height - 1 - inset);
            count += 2;
        }

        for (var y = inset; y < height - inset; y++)
        {
            total += Alpha(pixels, width, inset, y) + Alpha(pixels, width, width - 1 - inset, y);
            count += 2;
        }

        return count == 0 ? 1.0 : total / count;
    }

    private static double Alpha(byte[] pixels, int width, int x, int y) =>
        pixels[(y * width + x) * 4 + 3] >= 200 ? 1.0 : 0.0;

    // ---------------- 主色 ----------------

    /// <summary>按 4 bit/通道量化后取出现次数最多的颜色，同时返回它占全部不透明像素的比例。</summary>
    private static (Color Color, double Share) DominantColor(byte[] pixels, int width, int height)
    {
        var buckets = new Dictionary<int, (long Count, long R, long G, long B)>();
        long opaque = 0;

        for (var i = 0; i < width * height; i++)
        {
            var offset = i * 4;
            if (pixels[offset + 3] < 200) continue;

            opaque++;
            var b = pixels[offset];
            var g = pixels[offset + 1];
            var r = pixels[offset + 2];

            var key = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
            if (buckets.TryGetValue(key, out var acc))
            {
                buckets[key] = (acc.Count + 1, acc.R + r, acc.G + g, acc.B + b);
            }
            else
            {
                buckets[key] = (1, r, g, b);
            }
        }

        if (opaque == 0) return (Colors.Transparent, 0);

        var best = buckets.Values.OrderByDescending(v => v.Count).First();
        var color = Color.FromRgb(
            (byte)(best.R / best.Count),
            (byte)(best.G / best.Count),
            (byte)(best.B / best.Count));

        return (color, (double)best.Count / opaque);
    }

    private static Color AverageColor(byte[] pixels, int width, int height)
    {
        long count = 0, r = 0, g = 0, b = 0;

        for (var i = 0; i < width * height; i++)
        {
            var offset = i * 4;
            if (pixels[offset + 3] < 200) continue;

            count++;
            b += pixels[offset];
            g += pixels[offset + 1];
            r += pixels[offset + 2];
        }

        return count == 0
            ? Color.FromRgb(0x8A, 0x8A, 0x8E)
            : Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    // ---------------- 对比色底板 ----------------

    /// <summary>亮字形配深底、暗字形配浅底，保证字形跟底板有对比度；再压一点饱和度免得抢眼。</summary>
    private static Color BuildContrastPlate(Color glyph)
    {
        var luminance = Luminance(glyph);
        var target = luminance > 0.55 ? 0.30 : 0.90;
        var plate = AdjustLuminance(glyph, target);
        return Desaturate(plate, 0.25);
    }

    private static double Luminance(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static Color AdjustLuminance(Color c, double target)
    {
        var current = Luminance(c);

        if (target <= current)
        {
            var k = current < 0.02 ? 0 : target / current;
            return Color.FromRgb(
                (byte)Math.Clamp(c.R * k, 0, 255),
                (byte)Math.Clamp(c.G * k, 0, 255),
                (byte)Math.Clamp(c.B * k, 0, 255));
        }

        var t = (target - current) / Math.Max(0.02, 1 - current);
        return Blend(c, Colors.White, Math.Clamp(t, 0, 1));
    }

    private static Color Blend(Color from, Color to, double t) =>
        Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));

    private static Color Desaturate(Color c, double amount)
    {
        var gray = (byte)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
        return Blend(c, Color.FromRgb(gray, gray, gray), amount);
    }
}
