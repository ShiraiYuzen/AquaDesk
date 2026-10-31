using System.Windows.Media;

namespace AquaDesk.Models;

/// <summary>启动台里的一个应用条目。图标在后台线程提前 Freeze 好，可以直接跨线程用。</summary>
public sealed class LaunchpadItem
{
    public required string Name { get; init; }

    /// <summary>.lnk / .exe 路径，点击时交给 Shell 启动。</summary>
    public required string Target { get; init; }

    public ImageSource? Icon { get; init; }

    /// <summary>搜索用的归一化文本（小写）。</summary>
    public string SearchKey => Name.ToLowerInvariant();
}

/// <summary>底部的分页圆点。</summary>
public sealed class LaunchpadPageDot
{
    public int Index { get; init; }

    public bool IsCurrent { get; init; }
}
