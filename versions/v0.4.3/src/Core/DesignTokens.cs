namespace AquaDesk.Core;

/// <summary>
/// 全局设计 token。所有视觉参数从这里取，XAML 里不允许出现魔法数字。
/// 改动这里 = 全站换肤。
/// </summary>
public static class DesignTokens
{
    // ---- Dock 尺寸 ----
    public const double IconBox = 52;          // 图标基准边长
    public const double IconGap = 6;           // 图标左右外边距
    public const double DockPaddingX = 10;
    public const double DockPaddingY = 8;
    public const double DockCornerRadius = 22;
    public const double DockBottomMargin = 0;
    public const double DockWindowHeight = 120; // 给放大留出的窗口余量

    // ---- 放大镜 ----
    public const double MagnifyMaxScale = 1.55;  // 鼠标正下方最大放大倍数
    public const double MagnifySigma = 60;       // 影响半径（像素），越大波及越广

    // ---- 动效 ----
    public const int HoverAnimMs = 90;
    public const int ResetAnimMs = 140;
}
