using System.Runtime.InteropServices;
using AquaDesk.Core;

namespace AquaDesk.Services;

/// <summary>
/// 抓一块屏幕区域，写进调用方给的缓冲区（不每帧 new，避免 LOH 压力和 GC 抖动）。
///
/// 关键前提：Dock 窗口自己设了 <see cref="NativeMethods.WDA_EXCLUDEFROMCAPTURE"/>，
/// 所以 BitBlt 抓到的就是「Dock 背后」的画面。不设这个，抓到的会是 Dock 自己，
/// 折射层会把自己一遍遍拍进去，画面越来越亮。
/// </summary>
public static class ScreenSampler
{
    /// <summary>缓冲区至少要有 width * height * 4 字节。</summary>
    public static bool Capture(int x, int y, int width, int height, byte[] buffer)
    {
        if (width <= 0 || height <= 0) return false;
        if (buffer.Length < width * height * 4) return false;

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
        if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        var previous = NativeMethods.SelectObject(memDc, bitmap);

        try
        {
            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, x, y, NativeMethods.SRCCOPY))
                return false;

            var info = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,      // 负数 = 自上而下，省得后面翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0        // BI_RGB
            };

            return NativeMethods.GetDIBits(memDc, bitmap, 0, (uint)height, buffer, ref info, 0) != 0;
        }
        finally
        {
            NativeMethods.SelectObject(memDc, previous);
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
