using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AquaDesk.Models;

public enum LaunchpadEntryKind
{
    App,
    Folder
}

/// <summary>启动台网格里的一格：要么是一个应用，要么是一个文件夹。</summary>
public sealed class LaunchpadEntry : INotifyPropertyChanged
{
    private bool _isDropTarget;

    public LaunchpadEntryKind Kind { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>应用图标；文件夹为 null（用 Items 的缩略图代替）。</summary>
    public ImageSource? Icon { get; set; }

    /// <summary>应用对应的 .lnk / .exe；文件夹为 null。</summary>
    public string? Target { get; init; }

    /// <summary>文件夹里的应用。</summary>
    public ObservableCollection<LaunchpadItem> Items { get; } = new();

    public bool IsApp => Kind == LaunchpadEntryKind.App;

    public bool IsFolder => Kind == LaunchpadEntryKind.Folder;

    /// <summary>文件夹缩略图：最多取前 4 个应用。</summary>
    public IReadOnlyList<LaunchpadItem> Thumbnails =>
        Items.Count <= 4 ? Items : Items.Take(4).ToList();

    /// <summary>拖拽悬停高亮。</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            if (_isDropTarget == value) return;
            _isDropTarget = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTarget)));
        }
    }

    public static LaunchpadEntry FromApp(LaunchpadItem item) => new()
    {
        Kind = LaunchpadEntryKind.App,
        Name = item.Name,
        Icon = item.Icon,
        Target = item.Target
    };

    public static LaunchpadEntry CreateFolder(string name, IEnumerable<LaunchpadItem> items)
    {
        var folder = new LaunchpadEntry
        {
            Kind = LaunchpadEntryKind.Folder,
            Name = name
        };

        foreach (var item in items) folder.Items.Add(item);
        return folder;
    }

    /// <summary>文件夹里增删后刷新缩略图绑定。</summary>
    public void NotifyThumbnailsChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnails)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
