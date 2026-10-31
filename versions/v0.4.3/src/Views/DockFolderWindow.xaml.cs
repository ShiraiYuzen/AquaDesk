using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AquaDesk.Models;
using AquaDesk.Services;
using AquaDesk.ViewModels;

namespace AquaDesk.Views;

/// <summary>
/// Dock 上的文件夹打开后的样子 —— 视觉和交互都照启动台来（同一个暗底、7 列网格、
/// 图标 + 名字、悬停高亮、搜索、拖文件进来）。
///
/// 和启动台的区别：内容来源是这个文件夹自己的配置，不是开始菜单扫描；
/// 也没有分页和拖拽排序，条数多了直接滚。
/// </summary>
public partial class DockFolderWindow : Window
{
    private readonly DockViewModel _vm;
    private readonly DockItem _folder;

    private readonly List<LaunchpadItem> _all = new();
    private readonly ObservableCollection<LaunchpadItem> _visible = new();

    public DockFolderWindow(DockViewModel vm, DockItem folder)
    {
        _vm = vm;
        _folder = folder;

        InitializeComponent();
        ItemGrid.ItemsSource = _visible;

        TitleText.Text = folder.Name;

        Loaded += OnLoaded;
        PreviewKeyDown += OnKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        Activate();
        SearchBox.Focus();

        Reload();
    }

    /// <summary>从文件夹配置重新读一遍内容。</summary>
    private void Reload()
    {
        _all.Clear();

        foreach (var child in _folder.Children)
        {
            if (string.IsNullOrWhiteSpace(child.ShortcutPath)) continue;

            _all.Add(new LaunchpadItem
            {
                Name = child.Name,
                Target = child.ShortcutPath,
                Icon = child.Icon
            });
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var keyword = SearchBox.Text.Trim();

        _visible.Clear();

        foreach (var item in _all)
        {
            if (keyword.Length > 0
                && !item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _visible.Add(item);
        }

        SearchPlaceholder.Visibility = keyword.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        CountText.Text = _all.Count == 0
            ? "空文件夹"
            : $"{_all.Count} 个项目";

        EmptyHint.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResultHint.Visibility = _all.Count > 0 && _visible.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    // ---------------- 交互 ----------------

    private void OnEntryClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LaunchpadItem item }) return;

        Launch(item.Target);
    }

    private void Launch(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 文件夹启动失败 {target}: {ex.Message}");
        }

        Close();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    // ---------------- 右键菜单 ----------------

    private void OnMenuOpenEntry(object sender, RoutedEventArgs e)
    {
        if (ResolveEntry(sender) is { } entry) Launch(entry.Target);
    }

    private void OnMenuRemoveEntry(object sender, RoutedEventArgs e)
    {
        if (ResolveEntry(sender) is not { } entry) return;

        _vm.RemoveFromFolder(_folder, entry.Target);

        // 只改本地列表：_folder.Children 是打开窗口那一刻的快照，重读它反而会把刚删的项又捞回来。
        // Dock 上的图标在窗口关闭时统一重建。
        _all.RemoveAll(i => string.Equals(i.Target, entry.Target, StringComparison.OrdinalIgnoreCase));
        ApplyFilter();
    }

    /// <summary>ContextMenu 不在可视树里，DataContext 未必能拿到，从 PlacementTarget 上取。</summary>
    private static LaunchpadItem? ResolveEntry(object sender)
    {
        if (sender is not MenuItem menuItem) return null;
        if (menuItem.DataContext is LaunchpadItem direct) return direct;

        if (menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement target }
            && target.DataContext is LaunchpadItem fromTarget)
        {
            return fromTarget;
        }

        return null;
    }

    // ---------------- 拖文件进来 ----------------

    private void OnDragOverFiles(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void OnDropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        var added = false;

        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            if (!_vm.AddToFolder(_folder, file)) continue;

            // 同样只往本地列表里补，不重读 _folder.Children（那是旧快照，读回来新项就没了）
            _all.Add(new LaunchpadItem
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Target = file,
                Icon = IconExtractor.FromShortcut(file)
            });

            added = true;
        }

        if (added) ApplyFilter();

        e.Handled = true;
    }
}
