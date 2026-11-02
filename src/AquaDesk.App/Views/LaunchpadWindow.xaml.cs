using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AquaDesk.Models;
using AquaDesk.Services;

namespace AquaDesk.Views;

/// <summary>
/// 启动台：全屏应用网格 + 搜索 + 分页 + 拖拽排序/合并文件夹 + 拖入添加。
/// 应用列表从开始菜单扫，布局（顺序、文件夹、隐藏项）存在 launchpad.json。
/// </summary>
public partial class LaunchpadWindow : Window
{
    private const int Columns = 7;
    private const int Rows = 4;
    private const int PageSize = Columns * Rows;
    private const double DragThreshold = 8;

    private readonly List<LaunchpadItem> _apps = new();
    private readonly List<LaunchpadEntry> _entries = new();
    private readonly List<LaunchpadEntry> _filtered = new();
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<LaunchpadEntry> _visible = new();
    private readonly ObservableCollection<LaunchpadPageDot> _dots = new();

    private readonly List<string> _pendingFiles = new();

    private LaunchpadEntry? _openFolder;
    private string _keyword = string.Empty;
    private int _page;

    // 拖拽状态
    private LaunchpadEntry? _dragEntry;
    private Point _dragStart;
    private bool _dragging;
    private LaunchpadEntry? _dropTarget;
    private LaunchpadEntry? _hoverEntry;
    private DateTime _hoverSince;
    private bool _mergeArmed;
    private System.Windows.Threading.DispatcherTimer? _mergeTimer;
    private System.Windows.Threading.DispatcherTimer? _pageTimer;
    private Point? _pageDragStart;

    public LaunchpadWindow()
    {
        InitializeComponent();

        AppGrid.ItemsSource = _visible;
        PageDots.ItemsSource = _dots;

        Loaded += OnLoaded;
        PreviewKeyDown += OnKeyDown;
        MouseWheel += OnMouseWheel;
        MouseMove += OnWindowMouseMove;
        MouseLeftButtonUp += OnWindowMouseUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 铺满主屏（连任务栏一起盖住），DPI 缩放时 PrimaryScreen* 给的就是 DIP
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        Activate();
        SearchBox.Focus();

        StartScan();
    }

    // ---------------- 扫描与布局 ----------------

    /// <summary>
    /// 扫描放到后台 STA 线程：图标走 Shell COM，必须在 STA 里调；
    /// 每个图标 Freeze 之后就能直接跨线程给 UI 用。
    /// </summary>
    private void StartScan()
    {
        var thread = new Thread(() =>
        {
            var items = new List<LaunchpadItem>();

            try
            {
                foreach (var config in AppScanner.Scan(400))
                {
                    items.Add(new LaunchpadItem
                    {
                        Name = config.Name,
                        Target = config.Target,
                        Icon = IconExtractor.FromShortcut(config.Target)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AquaDesk] 启动台扫描失败：{ex.Message}");
            }

            Dispatcher.Invoke(() =>
            {
                _apps.Clear();
                _apps.AddRange(items);
                BuildLayout();
                FlushPendingFiles();
                ApplyFilter();
            });
        })
        {
            IsBackground = true,
            Name = "AquaDesk.LaunchpadScan"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// 用保存的布局重建格子：顺序和文件夹照搬，扫描到的新应用追加到末尾，
    /// 已经卸载掉的自动剔除。
    /// </summary>
    private void BuildLayout()
    {
        _entries.Clear();
        _hidden.Clear();

        var byTarget = new Dictionary<string, LaunchpadItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _apps)
        {
            if (!byTarget.ContainsKey(app.Target)) byTarget[app.Target] = app;
        }

        var layout = LaunchpadStore.Load();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (layout is not null)
        {
            foreach (var hidden in layout.Hidden) _hidden.Add(hidden);

            foreach (var entry in layout.Entries)
            {
                if (string.Equals(entry.Kind, "folder", StringComparison.OrdinalIgnoreCase))
                {
                    var items = new List<LaunchpadItem>();

                    foreach (var target in entry.Items)
                    {
                        if (target is null) continue;
                        if (!TryResolveItem(target, byTarget, out var item)) continue;
                        if (!used.Add(target)) continue;
                        items.Add(item);
                    }

                    if (items.Count >= 2)
                    {
                        _entries.Add(LaunchpadEntry.CreateFolder(entry.Name ?? "文件夹", items));
                    }
                    else if (items.Count == 1)
                    {
                        _entries.Add(LaunchpadEntry.FromApp(items[0]));
                    }
                }
                else if (entry.Target is not null
                         && TryResolveItem(entry.Target, byTarget, out var app)
                         && used.Add(entry.Target))
                {
                    _entries.Add(LaunchpadEntry.FromApp(app));
                }
            }
        }

        // 新装的应用追加到末尾，跳过用户隐藏过的
        foreach (var app in _apps)
        {
            if (used.Contains(app.Target)) continue;
            if (_hidden.Contains(app.Target)) continue;
            _entries.Add(LaunchpadEntry.FromApp(app));
        }
    }

    /// <summary>
    /// 按 target 解析出一个启动台条目。
    /// 优先用开始菜单扫描到的 app；扫不到（比如用户从桌面拖进来的 .lnk/.exe）时，
    /// 只要文件还在，就现场按文件名 + 图标重建一个 —— 否则重开启动台时这些手加的会被丢掉。
    /// </summary>
    private static bool TryResolveItem(
        string target, Dictionary<string, LaunchpadItem> byTarget, out LaunchpadItem item)
    {
        if (byTarget.TryGetValue(target, out var known))
        {
            item = known;
            return true;
        }

        if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
        {
            item = new LaunchpadItem
            {
                Name = Path.GetFileNameWithoutExtension(target),
                Target = target,
                Icon = IconExtractor.FromShortcut(target)
            };
            return true;
        }

        item = null!;
        return false;
    }

    private void SaveLayout()
    {
        var layout = new LaunchpadLayout { Hidden = _hidden.ToList() };

        foreach (var entry in _entries)
        {
            if (entry.IsFolder)
            {
                layout.Entries.Add(new LaunchpadLayoutEntry
                {
                    Kind = "folder",
                    Name = entry.Name,
                    Items = entry.Items.Select(i => i.Target).ToList()
                });
            }
            else
            {
                layout.Entries.Add(new LaunchpadLayoutEntry
                {
                    Kind = "app",
                    Target = entry.Target
                });
            }
        }

        LaunchpadStore.Save(layout);
    }

    // ---------------- 过滤与分页 ----------------

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _keyword = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _filtered.Clear();

        if (_openFolder is not null)
        {
            // 文件夹里不再分页也不再搜索，直接把内容铺出来
            foreach (var item in _openFolder.Items) _filtered.Add(LaunchpadEntry.FromApp(item));
        }
        else if (_keyword.Length == 0)
        {
            _filtered.AddRange(_entries);
        }
        else
        {
            foreach (var entry in _entries)
            {
                if (entry.Name.Contains(_keyword, StringComparison.OrdinalIgnoreCase))
                {
                    _filtered.Add(entry);
                }
            }
        }

        _page = 0;
        ShowPage();
    }

    private void ShowPage()
    {
        _visible.Clear();

        var start = _page * PageSize;
        for (var i = start; i < Math.Min(start + PageSize, _filtered.Count); i++)
        {
            _visible.Add(_filtered[i]);
        }

        var pageCount = _openFolder is not null
            ? 1
            : Math.Max(1, (_filtered.Count + PageSize - 1) / PageSize);

        _dots.Clear();
        for (var i = 0; i < pageCount; i++)
        {
            _dots.Add(new LaunchpadPageDot { Index = i, IsCurrent = i == _page });
        }

        if (_apps.Count == 0)
        {
            StatusText.Text = "正在扫描已安装的应用…";
            StatusText.Visibility = Visibility.Visible;
        }
        else if (_filtered.Count == 0)
        {
            StatusText.Text = _keyword.Length > 0 ? "没有匹配的应用" : "启动台是空的，从桌面拖个快捷方式进来";
            StatusText.Visibility = Visibility.Visible;
        }
        else
        {
            StatusText.Visibility = Visibility.Collapsed;
        }
    }

    private void TurnPage(int delta, bool animate = true)
    {
        if (_openFolder is not null) return;

        var pageCount = Math.Max(1, (_filtered.Count + PageSize - 1) / PageSize);
        var next = NetFxShims.Clamp(_page + delta, 0, pageCount - 1);
        if (next == _page) return;

        _page = next;
        ShowPage();
        if (animate) PlayPageTransition(delta > 0 ? 1 : -1);
    }

    /// <summary>拖动超过阈值松手：当前页滑出 → 换页 → 新页从另一侧滑入。</summary>
    private void CommitDragPage(int direction)
    {
        var pageCount = Math.Max(1, (_filtered.Count + PageSize - 1) / PageSize);
        if (NetFxShims.Clamp(_page + direction, 0, pageCount - 1) == _page)
        {
            SnapBackPage();
            return;
        }

        var width = ActualWidth > 0 ? ActualWidth : 1920;

        var slideOut = new DoubleAnimation(direction > 0 ? -width : width, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };

        slideOut.Completed += (_, _) =>
        {
            TurnPage(direction, animate: false);

            PageSlide.BeginAnimation(TranslateTransform.XProperty, null);
            PageSlide.X = direction > 0 ? width : -width;

            AppGrid.BeginAnimation(UIElement.OpacityProperty, null);
            AppGrid.Opacity = 1;

            var slideIn = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            PageSlide.BeginAnimation(TranslateTransform.XProperty, slideIn);
        };

        PageSlide.BeginAnimation(TranslateTransform.XProperty, slideOut);
    }

    /// <summary>没拖够：弹回原位。</summary>
    private void SnapBackPage()
    {
        var spring = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        spring.Completed += (_, _) =>
        {
            PageSlide.BeginAnimation(TranslateTransform.XProperty, null);
            PageSlide.X = 0;
        };

        PageSlide.BeginAnimation(TranslateTransform.XProperty, spring);
        AppGrid.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    /// <summary>翻页时整页从侧面滑进来 + 淡入，别硬切。</summary>
    private void PlayPageTransition(int direction)
    {
        if (direction == 0) return;

        PageSlide.X = direction * 60;
        AppGrid.Opacity = 0;

        var slide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220));

        PageSlide.BeginAnimation(TranslateTransform.XProperty, slide);
        AppGrid.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ---------------- 打开 / 关闭 ----------------

    private void ShowFolder(LaunchpadEntry folder)
    {
        _openFolder = folder;
        BackButton.Visibility = Visibility.Visible;
        SearchBox.Text = string.Empty;
        ApplyFilter();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnBackClick(object sender, RoutedEventArgs e) => CloseFolder();

    private void CloseFolder()
    {
        if (_openFolder is null) return;

        _openFolder = null;
        BackButton.Visibility = Visibility.Collapsed;
        ApplyFilter();
    }

    private void OnEntryClick(object sender, MouseButtonEventArgs e)
    {
        // 拖拽结束时的松手别当成点击
        if (_dragging) return;

        if (sender is not FrameworkElement { DataContext: LaunchpadEntry entry }) return;

        if (entry.IsFolder)
        {
            ShowFolder(entry);
            return;
        }

        Launch(entry.Target);
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
            Debug.WriteLine($"[AquaDesk] 启动台启动失败 {target}: {ex.Message}");
        }

        Close();
    }

    // ---------------- 拖拽排序 / 合并文件夹 ----------------

    private void OnEntryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LaunchpadEntry entry }) return;

        _dragEntry = entry;
        _dragStart = e.GetPosition(this);
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        // 空白处左右拖动：页面跟着手走，松手时再看拖够没拖够
        if (_dragEntry is null && _pageDragStart is { } start
            && e.LeftButton == MouseButtonState.Pressed)
        {
            var dx = e.GetPosition(this).X - start.X;
            var limit = ActualWidth > 0 ? ActualWidth : 1920;

            PageSlide.BeginAnimation(TranslateTransform.XProperty, null);
            PageSlide.X = NetFxShims.Clamp(dx, -limit, limit);

            // 越拖越淡，给一点「正在翻页」的反馈
            AppGrid.Opacity = Math.Max(0.35, 1 - Math.Abs(dx) / (limit * 1.6));

            return;
        }

        if (_dragEntry is null) return;

        if (!_dragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragEntry = null;
                return;
            }

            var current = e.GetPosition(this);
            if (Math.Abs(current.X - _dragStart.X) < DragThreshold
                && Math.Abs(current.Y - _dragStart.Y) < DragThreshold)
            {
                return;
            }

            BeginDrag();
        }

        UpdateDrag(e.GetPosition(this));
    }

    private void BeginDrag()
    {
        if (_dragEntry is null) return;

        _dragging = true;

        DragGhost.Source = _dragEntry.IsFolder
            ? _dragEntry.Thumbnails.FirstOrDefault()?.Icon
            : _dragEntry.Icon;
        DragGhost.Visibility = Visibility.Visible;

        // 鼠标停在某个图标上不动时不会有 MouseMove，靠定时器来判断「停留够久」
        _mergeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _mergeTimer.Tick += (_, _) => CheckMergeHover();
        _mergeTimer.Start();
    }

    private void UpdateDrag(Point position)
    {
        Canvas.SetLeft(DragGhost, position.X - DragGhost.Width / 2);
        Canvas.SetTop(DragGhost, position.Y - DragGhost.Height / 2);

        // 文件夹里拖动只支持「拖出去」，不做内部排序
        if (_openFolder is not null)
        {
            DragOutHint.Visibility = IsOutsideGrid(position) ? Visibility.Visible : Visibility.Collapsed;
            DropBar.Visibility = Visibility.Collapsed;
            return;
        }

        var target = EntryAt(position);
        if (ReferenceEquals(target, _dragEntry)) target = null;

        // 悬停在同一个图标上超过 600ms 才算「要合并成文件夹」，
        // 直接拖过去松手是排序——不然网格铺满时根本没法调整顺序。
        if (!ReferenceEquals(target, _hoverEntry))
        {
            _hoverEntry = target;
            _hoverSince = DateTime.Now;
            _mergeArmed = false;

            if (_dropTarget is not null) _dropTarget.IsDropTarget = false;
            _dropTarget = null;
        }
        else if (target is not null && !_mergeArmed
                 && (DateTime.Now - _hoverSince).TotalMilliseconds > 600)
        {
            _mergeArmed = true;
            _dropTarget = target;
            target.IsDropTarget = true;
        }

        if (_mergeArmed)
        {
            DropBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateDropIndicator(DropIndex(position));
        }

        CheckEdgePaging(position);
    }

    /// <summary>把插入位置画成一根竖条，用户能看清松手会插到哪。</summary>
    private void UpdateDropIndicator(int index)
    {
        if (_visible.Count == 0)
        {
            DropBar.Visibility = Visibility.Collapsed;
            return;
        }

        var atEnd = index >= _visible.Count;
        var anchorIndex = atEnd ? _visible.Count - 1 : Math.Max(0, index);

        if (AppGrid.ItemContainerGenerator.ContainerFromItem(_visible[anchorIndex]) is not FrameworkElement anchor)
        {
            DropBar.Visibility = Visibility.Collapsed;
            return;
        }

        var origin = anchor.TranslatePoint(new Point(0, 0), (UIElement)DropLayer.Parent);

        DropBar.Height = Math.Max(48, anchor.ActualHeight - 24);
        Canvas.SetLeft(DropBar, atEnd ? origin.X + anchor.ActualWidth - 9 : origin.X + 4);
        Canvas.SetTop(DropBar, origin.Y + 12);
        DropBar.Visibility = Visibility.Visible;
    }

    /// <summary>拖到左右边缘停一下自动翻页，支持跨页搬图标。</summary>
    private void CheckEdgePaging(Point position)
    {
        if (_openFolder is not null) return;

        var origin = AppGrid.TranslatePoint(new Point(0, 0), this);
        var right = origin.X + AppGrid.ActualWidth;

        var direction = 0;
        if (position.X < origin.X + 60) direction = -1;
        else if (position.X > right - 60) direction = 1;

        if (direction == 0)
        {
            _pageTimer?.Stop();
            _pageTimer = null;
            return;
        }

        if (_pageTimer is not null) return;

        _pageTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };

        _pageTimer.Tick += (_, _) =>
        {
            _pageTimer?.Stop();
            _pageTimer = null;
            TurnPage(direction);
        };

        _pageTimer.Start();
    }

    private bool IsOutsideGrid(Point windowPoint)
    {
        var origin = AppGrid.TranslatePoint(new Point(0, 0), this);
        return !new Rect(origin, AppGrid.RenderSize).Contains(windowPoint);
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pageDragStart is { } pageStart)
        {
            _pageDragStart = null;

            var dx = e.GetPosition(this).X - pageStart.X;
            var threshold = Math.Max(120, ActualWidth * 0.18);

            if (Math.Abs(dx) >= threshold) CommitDragPage(dx < 0 ? 1 : -1);
            else SnapBackPage();

            return;
        }

        if (_dragEntry is null) return;

        var entry = _dragEntry;
        var wasDragging = _dragging;
        var target = _dropTarget;
        var mergeArmed = _mergeArmed;
        var dropPoint = e.GetPosition(this);

        EndDrag();

        if (!wasDragging) return;

        // 拖拽结束时不要再触发 OnEntryClick
        e.Handled = true;

        // 文件夹里：拖到网格外就移出
        if (_openFolder is not null)
        {
            if (IsOutsideGrid(dropPoint)) MoveOutOfFolder(entry);
            return;
        }

        // 只有「停留够久」才合并，否则一律按落点排序
        if (!mergeArmed || target is null)
        {
            Reorder(entry, dropPoint);
            return;
        }

        if (target.IsFolder)
        {
            AddToFolder(target, entry);
            return;
        }

        MergeIntoFolder(entry, target);
    }

    private void CheckMergeHover()
    {
        if (!_dragging || _mergeArmed || _hoverEntry is null) return;
        if ((DateTime.Now - _hoverSince).TotalMilliseconds < 600) return;

        _mergeArmed = true;
        _dropTarget = _hoverEntry;
        _hoverEntry.IsDropTarget = true;
        DropBar.Visibility = Visibility.Collapsed;
    }

    private void EndDrag()
    {
        _mergeTimer?.Stop();
        _mergeTimer = null;

        if (_dropTarget is not null)
        {
            _dropTarget.IsDropTarget = false;
            _dropTarget = null;
        }

        _dragEntry = null;
        _dragging = false;
        _mergeArmed = false;
        _hoverEntry = null;

        _pageTimer?.Stop();
        _pageTimer = null;

        DragGhost.Visibility = Visibility.Collapsed;
        DropBar.Visibility = Visibility.Collapsed;
        DragOutHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>把应用从当前文件夹拖回主页；文件夹只剩一个时自动解散。</summary>
    private void MoveOutOfFolder(LaunchpadEntry entry)
    {
        var folder = _openFolder;
        if (folder is null || entry.Target is null) return;

        var item = folder.Items.FirstOrDefault(
            i => string.Equals(i.Target, entry.Target, StringComparison.OrdinalIgnoreCase));

        if (item is null) return;

        folder.Items.Remove(item);
        folder.NotifyThumbnailsChanged();

        // 只剩一个（或空了）就自动取消文件夹，剩下的放回主页
        if (folder.Items.Count <= 1)
        {
            var index = _entries.IndexOf(folder);
            if (index >= 0)
            {
                _entries.RemoveAt(index);
                foreach (var remain in folder.Items.ToList())
                {
                    _entries.Insert(index++, LaunchpadEntry.FromApp(remain));
                }
            }

            _openFolder = null;
            BackButton.Visibility = Visibility.Collapsed;
        }

        _entries.Add(LaunchpadEntry.FromApp(item));

        SaveLayout();
        ApplyFilter();
    }

    private void Reorder(LaunchpadEntry entry, Point windowPoint)
    {
        var from = _entries.IndexOf(entry);
        if (from < 0) return;

        // DropIndex 给的是当前页里的位置，要换算成 _entries 的全局索引
        var to = _page * PageSize + DropIndex(windowPoint);
        if (to > from) to--;

        to = NetFxShims.Clamp(to, 0, _entries.Count - 1);
        if (to == from) return;

        _entries.RemoveAt(from);
        _entries.Insert(to, entry);

        SaveLayout();
        ApplyFilter();
    }

    private void AddToFolder(LaunchpadEntry folder, LaunchpadEntry entry)
    {
        if (entry.Target is null) return;

        folder.Items.Add(new LaunchpadItem
        {
            Name = entry.Name,
            Target = entry.Target,
            Icon = entry.Icon
        });

        folder.NotifyThumbnailsChanged();
        _entries.Remove(entry);

        SaveLayout();
        ApplyFilter();
    }

    private void MergeIntoFolder(LaunchpadEntry first, LaunchpadEntry second)
    {
        if (first.Target is null || second.Target is null) return;

        var items = new List<LaunchpadItem>
        {
            new() { Name = first.Name, Target = first.Target, Icon = first.Icon },
            new() { Name = second.Name, Target = second.Target, Icon = second.Icon }
        };

        var folder = LaunchpadEntry.CreateFolder("文件夹", items);

        var index = _entries.IndexOf(second);
        _entries.Remove(second);
        _entries.Remove(first);
        _entries.Insert(NetFxShims.Clamp(index, 0, _entries.Count), folder);

        SaveLayout();
        ApplyFilter();
    }

    private LaunchpadEntry? EntryAt(Point windowPoint)
    {
        var point = TranslatePoint(windowPoint, AppGrid);

        foreach (var entry in _visible)
        {
            if (AppGrid.ItemContainerGenerator.ContainerFromItem(entry) is not FrameworkElement container)
                continue;

            var origin = container.TranslatePoint(new Point(0, 0), AppGrid);
            if (new Rect(origin, container.RenderSize).Contains(point)) return entry;
        }

        return null;
    }

    private int DropIndex(Point windowPoint)
    {
        var point = TranslatePoint(windowPoint, AppGrid);

        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        FrameworkElement? bestContainer = null;

        for (var i = 0; i < _visible.Count; i++)
        {
            if (AppGrid.ItemContainerGenerator.ContainerFromItem(_visible[i]) is not FrameworkElement container)
                continue;

            var origin = container.TranslatePoint(new Point(0, 0), AppGrid);
            var dx = point.X - (origin.X + container.ActualWidth / 2);
            var dy = point.Y - (origin.Y + container.ActualHeight / 2);
            var distance = dx * dx + dy * dy;

            if (distance >= bestDistance) continue;

            bestDistance = distance;
            bestIndex = i;
            bestContainer = container;
        }

        if (bestIndex < 0 || bestContainer is null) return _visible.Count;

        var anchor = bestContainer.TranslatePoint(new Point(0, 0), AppGrid);
        var rightHalf = point.X > anchor.X + bestContainer.ActualWidth / 2;

        return rightHalf ? bestIndex + 1 : bestIndex;
    }

    // ---------------- 右键菜单 ----------------

    private void OnMenuOpenEntry(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LaunchpadEntry entry }) return;

        if (entry.IsFolder) ShowFolder(entry);
        else Launch(entry.Target);
    }

    private void OnMenuRemoveEntry(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LaunchpadEntry entry }) return;

        if (entry.IsFolder)
        {
            foreach (var item in entry.Items) _hidden.Add(item.Target);
        }
        else if (entry.Target is not null)
        {
            _hidden.Add(entry.Target);
        }

        _entries.Remove(entry);

        SaveLayout();
        ApplyFilter();
    }

    private void OnMenuDissolveFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: LaunchpadEntry entry } || !entry.IsFolder) return;

        var index = _entries.IndexOf(entry);
        if (index < 0) return;

        _entries.RemoveAt(index);

        foreach (var item in entry.Items)
        {
            _entries.Insert(index++, LaunchpadEntry.FromApp(item));
        }

        SaveLayout();
        ApplyFilter();
    }

    // ---------------- 从桌面拖入 ----------------

    private void OnDragOverFiles(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void OnDropFiles(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        AddFiles(files);
        e.Handled = true;
    }

    /// <summary>把文件加进启动台。扫描还没结束时先排队，扫完再落位。</summary>
    public void AddFiles(IEnumerable<string> files)
    {
        foreach (var file in files) _pendingFiles.Add(file);

        if (_apps.Count > 0) FlushPendingFiles();
    }

    private void FlushPendingFiles()
    {
        if (_pendingFiles.Count == 0) return;

        var files = _pendingFiles.ToList();
        _pendingFiles.Clear();

        var added = false;

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsAlreadyPresent(file)) continue;

            _hidden.Remove(file);

            _entries.Add(LaunchpadEntry.FromApp(new LaunchpadItem
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Target = file,
                Icon = IconExtractor.FromShortcut(file)
            }));

            added = true;
        }

        if (!added) return;

        SaveLayout();
        ApplyFilter();
    }

    /// <summary>
    /// 启动台里是否已经有这个应用。
    /// 从桌面拖进来的快捷方式和开始菜单里的那条路径不同（两个不同的 .lnk），
    /// 只比路径会漏掉，所以按「真实 exe」比对 —— 指向同一个程序就算已有，不重复添加。
    /// </summary>
    private bool IsAlreadyPresent(string droppedFile)
    {
        var droppedTarget = ResolveRealTarget(droppedFile);

        bool Match(string? target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            if (string.Equals(target, droppedFile, StringComparison.OrdinalIgnoreCase)) return true;

            var real = ResolveRealTarget(target);
            return !string.IsNullOrEmpty(real)
                && string.Equals(real, droppedTarget, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var entry in _entries)
        {
            if (entry.IsFolder)
            {
                if (entry.Items.Any(i => Match(i.Target))) return true;
            }
            else if (Match(entry.Target))
            {
                return true;
            }
        }

        return _apps.Any(a => Match(a.Target));
    }

    /// <summary>把 .lnk 解到它指向的真实 exe；解不到（或本来就是 exe）就返回原路径。</summary>
    private static string ResolveRealTarget(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ShortcutResolver.ResolveTargetPath(path);
            if (!string.IsNullOrEmpty(resolved)) return resolved;
        }
        return path;
    }

    // ---------------- 键盘 / 鼠标 ----------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_openFolder is not null) CloseFolder();
                else Close();
                e.Handled = true;
                break;

            case Key.Right:
            case Key.PageDown:
                TurnPage(1);
                e.Handled = true;
                break;

            case Key.Left:
            case Key.PageUp:
                TurnPage(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                if (_visible.Count > 0)
                {
                    var first = _visible[0];
                    if (first.IsFolder) ShowFolder(first);
                    else Launch(first.Target);
                    e.Handled = true;
                }
                break;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TurnPage(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }


    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 启动台是模态的：点空白不会退出，只有 Esc 才关。
        e.Handled = true;

        // 在背景空白上按下 → 允许左右拖动翻页
        _pageDragStart = ReferenceEquals(e.OriginalSource, RootGrid)
            ? e.GetPosition(this)
            : null;
    }
}
