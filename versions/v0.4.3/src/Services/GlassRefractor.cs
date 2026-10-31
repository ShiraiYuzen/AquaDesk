namespace AquaDesk.Services;

/// <summary>
/// 把抓到的背景画面做成「玻璃折射」：降采样 → 轻微模糊 → 只在边缘一圈按法线方向外推采样点。
///
/// 性能上做了三件事（早期版本每帧在 UI 线程全分辨率算，拖窗口时卡成幻灯片）：
/// 1. 先降采样再算，像素量降到 1/4；
/// 2. 所有中间缓冲复用，不每帧 new；
/// 3. 输出帧本身很小（半分辨率，约 35KB），走 Gen0，不进大对象堆。
///
/// 为什么只推边缘：真玻璃中间平、边缘弧，光线只在边缘偏折。整块都推就成了放大镜。
/// </summary>
public sealed class GlassRefractor
{
    /// <summary>降采样后用的模糊半径。半分辨率下 2 ≈ 全分辨率 4。</summary>
    private const int BlurRadius = 2;

    private byte[] _small = Array.Empty<byte>();
    private byte[] _temp = Array.Empty<byte>();
    private byte[] _blurred = Array.Empty<byte>();

    /// <summary>返回的帧归调用方所有（每次新分配，约 35KB）。</summary>
    public byte[]? Render(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int step,
        int outputWidth,
        int outputHeight,
        int padding,
        double cornerRadius,
        double edgeWidth,
        double pushStrength)
    {
        if (step < 1) step = 1;
        if (outputWidth <= 0 || outputHeight <= 0) return null;

        var smallWidth = (sourceWidth + step - 1) / step;
        var smallHeight = (sourceHeight + step - 1) / step;
        var smallLength = smallWidth * smallHeight * 4;

        if (_small.Length < smallLength)
        {
            _small = new byte[smallLength];
            _temp = new byte[smallLength];
            _blurred = new byte[smallLength];
        }

        Downsample(source, sourceWidth, sourceHeight, step, smallWidth, smallHeight, _small);
        Blur(_small, smallWidth, smallHeight, _temp, _blurred);

        var dockWidth = outputWidth * step;
        var dockHeight = outputHeight * step;
        var centerX = dockWidth / 2.0;
        var centerY = dockHeight / 2.0;
        var halfW = dockWidth / 2.0 - cornerRadius;
        var halfH = dockHeight / 2.0 - cornerRadius;
        var edge = Math.Max(2.0, edgeWidth);
        var push = edge * pushStrength;

        var target = new byte[outputWidth * outputHeight * 4];
        var stepInv = 1.0 / step;

        for (var oy = 0; oy < outputHeight; oy++)
        {
            var dockY = oy * step + step * 0.5;
            var py = dockY - centerY;
            var absY = Math.Abs(py) - halfH;

            for (var ox = 0; ox < outputWidth; ox++)
            {
                var index = (oy * outputWidth + ox) * 4;
                var dockX = ox * step + step * 0.5;
                var px = dockX - centerX;
                var absX = Math.Abs(px) - halfW;

                // 圆角矩形的有符号距离场：<0 在里面
                var outsideX = Math.Max(absX, 0);
                var outsideY = Math.Max(absY, 0);
                var outside = Math.Sqrt(outsideX * outsideX + outsideY * outsideY);
                var inside = Math.Min(Math.Max(absX, absY), 0);
                var distance = -(outside + inside - cornerRadius);

                if (distance <= 0)
                {
                    // 形状外：留空，靠圆角裁切兜住
                    continue;
                }

                var sampleX = dockX + padding;
                var sampleY = dockY + padding;

                if (distance < edge)
                {
                    // 越靠近边缘推得越多（平方曲线，内圈几乎不动）
                    var t = 1 - distance / edge;
                    var amount = t * t * push;

                    var length = Math.Sqrt(px * px + py * py);
                    if (length > 0.001)
                    {
                        sampleX += px / length * amount;
                        sampleY += py / length * amount;
                    }
                }

                var sx = Math.Clamp((int)(sampleX * stepInv), 0, smallWidth - 1);
                var sy = Math.Clamp((int)(sampleY * stepInv), 0, smallHeight - 1);

                var sourceIndex = (sy * smallWidth + sx) * 4;
                target[index] = _blurred[sourceIndex];
                target[index + 1] = _blurred[sourceIndex + 1];
                target[index + 2] = _blurred[sourceIndex + 2];
                target[index + 3] = 255;
            }
        }

        return target;
    }

    private static void Downsample(
        byte[] source, int sourceWidth, int sourceHeight,
        int step, int smallWidth, int smallHeight, byte[] target)
    {
        for (var y = 0; y < smallHeight; y++)
        {
            var sourceY = Math.Min(y * step + step / 2, sourceHeight - 1);
            var sourceRow = sourceY * sourceWidth;

            for (var x = 0; x < smallWidth; x++)
            {
                var sourceX = Math.Min(x * step + step / 2, sourceWidth - 1);
                var from = (sourceRow + sourceX) * 4;
                var to = (y * smallWidth + x) * 4;

                target[to] = source[from];
                target[to + 1] = source[from + 1];
                target[to + 2] = source[from + 2];
                target[to + 3] = source[from + 3];
            }
        }
    }

    /// <summary>可分离的盒式模糊，两趟（横、纵），比高斯快得多。</summary>
    private static void Blur(byte[] source, int width, int height, byte[] temp, byte[] result)
    {
        var window = BlurRadius * 2 + 1;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;

            for (var channel = 0; channel < 4; channel++)
            {
                var sum = 0;

                for (var k = -BlurRadius; k <= BlurRadius; k++)
                {
                    var x = Math.Clamp(k, 0, width - 1);
                    sum += source[(row + x) * 4 + channel];
                }

                for (var x = 0; x < width; x++)
                {
                    temp[(row + x) * 4 + channel] = (byte)(sum / window);

                    var addX = Math.Clamp(x + BlurRadius + 1, 0, width - 1);
                    var removeX = Math.Clamp(x - BlurRadius, 0, width - 1);
                    sum += source[(row + addX) * 4 + channel] - source[(row + removeX) * 4 + channel];
                }
            }
        }

        for (var x = 0; x < width; x++)
        {
            for (var channel = 0; channel < 4; channel++)
            {
                var sum = 0;

                for (var k = -BlurRadius; k <= BlurRadius; k++)
                {
                    var y = Math.Clamp(k, 0, height - 1);
                    sum += temp[(y * width + x) * 4 + channel];
                }

                for (var y = 0; y < height; y++)
                {
                    result[(y * width + x) * 4 + channel] = (byte)(sum / window);

                    var addY = Math.Clamp(y + BlurRadius + 1, 0, height - 1);
                    var removeY = Math.Clamp(y - BlurRadius, 0, height - 1);
                    sum += temp[(addY * width + x) * 4 + channel] - temp[(removeY * width + x) * 4 + channel];
                }
            }
        }
    }
}
