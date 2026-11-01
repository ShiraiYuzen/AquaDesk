using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AquaDesk.Models;
using AquaDesk.Services;

namespace AquaDesk.ViewModels;

public sealed class DockViewModel : INotifyPropertyChanged
{
    private readonly List<DockItemConfig> _configs = new();

    private int _previewSlotLimit = WindowPreviewService.DefaultPreviewSlots;

    /// <summary>
    /// 预览区当前能放几格（含末尾的「全部窗口」入口）。
    /// 由 DockWindow 按 Dock 可用宽度算出来塞进来 —— 宽度只有它知道。
    /// </summary>
    public int PreviewSlotLimit
    {
        get => _previewSlotLimit;
        set
        {
            var clamped = Math.Clamp(value, 3, WindowPreviewService.MaxPreviewSlots);
            if (clamped == _previewSlotLimit) return;

            _previewSlotLimit = clamped;
            App.Log($"Dock：预览格数改为 {clamped}", null);

            // 故意在这里**不**重建预览集合。
            // setter 是从 DockWindow.UpdateWindowWidth 里调的，重建会改 Items，
            // 而 Items 一变又回头触发宽度计算 —— 容易绕成环。
            // 交给下一次窗口轮询（≤800ms）自然按新格数重建，宽度也随之更新。
        }
    }

    /// <summary>
    /// 按应用分好组的全部窗口（不截断）。「全部窗口」菜单拿它列出被折叠的应用。
    /// </summary>
    public IReadOnlyList<WindowGroup> WindowGroups { get; private set; } = Array.Empty<WindowGroup>();

    private const string SystemBrand = "Brand";
    private const string SystemWindows = "Windows";
    private const string SystemLaunchpad = "Launchpad";
    private const string SystemComputer = "Computer";

    /// <summary>系统图标的默认顺序，用来决定「重新打开某个图标时插回哪儿」。</summary>
    private static readonly string[] DefaultSystemOrder =
        { SystemBrand, SystemWindows, SystemLaunchpad, SystemComputer };

    public DockViewModel(AppSettings settings)
    {
        Settings = settings;

        var loaded = DockStore.Load();

        if (loaded is null)
        {
            // 首次启动：拿开始菜单前几个应用做初始内容，之后完全由用户增删
            _configs.AddRange(BuildSeed());
        }
        else
        {
            // 文件夹的 Target 是空的，不能按 Target 过滤掉，否则重启后文件夹会消失
            _configs.AddRange(loaded.Where(c =>
                c.IsFolder || !string.IsNullOrEmpty(c.System) || !string.IsNullOrWhiteSpace(c.Target)));
        }

        // 系统图标以前是写死的、不进 dock.json，这里给老配置补上
        var migrated = SyncSystemItems();
        if (loaded is null || migrated) DockStore.Save(_configs);

        Rebuild();
    }

    public AppSettings Settings { get; }

    /// <summary>
    /// 让 _configs 里的系统图标和设置页的开关保持一致。
    /// 已经排好的顺序保持不动 —— 用户把「我的电脑」拖到中间，下次开关别的图标不该把它弹回去。
    /// 返回是否改动了配置（改动了就要落盘）。
    /// </summary>
    private bool SyncSystemItems()
    {
        var changed = false;

        // 认不出来的 System 值（手工改坏 json）也一并清掉，否则会和 Items 错位
        changed |= _configs.RemoveAll(c =>
            c.System is { } kind && !DefaultSystemOrder.Contains(kind)) > 0;

        // 关掉的移出配置
        changed |= _configs.RemoveAll(c => c.System is { } kind && !IsSystemEnabled(kind)) > 0;

        // 开着的补回来：插在它默认前一个的后面，没有就插最前
        for (var i = 0; i < DefaultSystemOrder.Length; i++)
        {
            var kind = DefaultSystemOrder[i];

            if (!IsSystemEnabled(kind)) continue;
            if (_configs.Exists(c => c.System == kind)) continue;

            var index = 0;

            for (var j = i - 1; j >= 0; j--)
            {
                var previous = _configs.FindIndex(c => c.System == DefaultSystemOrder[j]);
                if (previous < 0) continue;

                index = previous + 1;
                break;
            }

            _configs.Insert(index, new DockItemConfig { Name = SystemName(kind), System = kind });
            changed = true;
        }

        return changed;
    }

    private bool IsSystemEnabled(string kind) => kind switch
    {
        // 这两个入口已经搬到状态胶囊上，Dock 上永远不摆。
        // ShowBrandIcon / ShowWindowsIcon 现在管的是胶囊上显不显示，别再拿到这里用。
        // 老配置里如果还残留这两项，RefreshStructure 会按 false 把它们剔掉。
        SystemBrand => false,
        SystemWindows => false,
        SystemLaunchpad => Settings.ShowLaunchpadIcon,
        SystemComputer => Settings.ShowComputerIcon,
        _ => false
    };

    private static string SystemName(string kind) => kind switch
    {
        SystemBrand => "AquaDesk",
        SystemWindows => "Windows 菜单",
        SystemLaunchpad => "启动台",
        SystemComputer => "我的电脑",
        _ => kind
    };

    /// <summary>尾部系统项个数：分隔线 + 废纸篓（关掉废纸篓时两个都不加）。</summary>
    public int TailCount
    {
        get
        {
            var count = 0;
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (!Items[i].IsTail) break;
                count++;
            }
            return count;
        }
    }

    public ObservableCollection<DockItem> Items { get; } = new();

    /// <summary>
    /// 当前展示的窗口预览。它们排在应用区和废纸篓之间，不属于 _configs，
    /// 所以数量变化不影响「用户图标」的索引换算。
    /// </summary>
    private readonly List<DockItem> _previews = new();

    /// <summary>预览区末尾的「全部窗口」入口；没有折叠时为 null。</summary>
    private DockItem? _overflowItem;

    public IReadOnlyList<DockItem> Previews => _previews;

    /// <summary>用户图标个数（应用 + 文件夹，不含窗口预览和两端的系统项）。</summary>
    public int AppCount
    {
        get
        {
            var count = 0;
            foreach (var item in Items)
            {
                if (item.IsApp || item.IsFolder) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// 配置区在 Items 里的结束位置（不含）。
    /// 现在系统图标（品牌 / Windows / 启动台 / 我的电脑）也排在 _configs 里，
    /// 所以 Items 的排布就是 [_configs][尾部]，没有单独的头部区了。
    /// </summary>
    public int AppEnd => _configs.Count;

    public bool IsEmpty => AppCount == 0;

    /// <summary>
    /// 设置窗口是否开着。品牌图标本身没有进程，但设置页打开期间要让它下方亮起运行指示条，
    /// 这样一眼能看出「AquaDesk 自己正在跑」。
    /// </summary>
    public bool IsSettingsOpen { get; set; }

    // ---------------- 增删 ----------------

    /// <summary>添加一个 .exe / .lnk 到末尾。重复路径直接忽略，取不到图标的也忽略。</summary>
    public bool Add(string targetPath) => Insert(_configs.Count, targetPath);

    /// <summary>插到指定位置（拖放时按落点插入）。</summary>
    public bool Insert(int index, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return false;
        if (_configs.Any(c => string.Equals(c.Target, targetPath, StringComparison.OrdinalIgnoreCase)))
            return false;

        var item = CreateItem(Path.GetFileNameWithoutExtension(targetPath), targetPath);
        if (item is null) return false;

        index = Math.Clamp(index, 0, _configs.Count);

        _configs.Insert(index, new DockItemConfig { Name = item.Name, Target = targetPath });
        Items.Insert(index, item);

        DockStore.Save(_configs);
        return true;
    }

    public void Remove(DockItem item)
    {
        // 文件夹的 Target 是空串，靠路径匹配会误删第一个空 Target 的项，
        // 所以统一走索引：Items 的排布恒为 [头部系统项][_configs][尾部系统项]
        var index = Items.IndexOf(item);

        if (index >= 0 && index < _configs.Count) _configs.RemoveAt(index);
        Items.Remove(item);

        DockStore.Save(_configs);
    }

    /// <summary>新建一个空文件夹，插到应用区末尾（分隔线之前）。</summary>
    public DockItem AddFolder()
    {
        var config = new DockItemConfig
        {
            Name = NextFolderName(),
            IsFolder = true,
            Items = new List<DockItemConfig>()
        };

        _configs.Add(config);

        var item = DockItem.CreateFolder(config.Name, Array.Empty<DockItem>());
        Items.Insert(_configs.Count - 1, item);

        DockStore.Save(_configs);
        return item;
    }

    /// <summary>把一个文件 / 程序塞进文件夹。路径重复直接忽略。</summary>
    public bool AddToFolder(DockItem folder, string path)
    {
        var config = FolderConfig(folder);
        if (config is null) return false;
        if (string.IsNullOrWhiteSpace(path)) return false;

        config.Items ??= new List<DockItemConfig>();

        if (config.Items.Any(c => string.Equals(c.Target, path, StringComparison.OrdinalIgnoreCase)))
            return false;

        config.Items.Add(new DockItemConfig
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Target = path
        });

        DockStore.Save(_configs);
        return true;
    }

    /// <summary>从文件夹里拿掉一项（按路径）。</summary>
    public bool RemoveFromFolder(DockItem folder, string target)
    {
        var config = FolderConfig(folder);
        if (config?.Items is null) return false;

        var removed = config.Items.RemoveAll(
            c => string.Equals(c.Target, target, StringComparison.OrdinalIgnoreCase)) > 0;

        if (removed) DockStore.Save(_configs);
        return removed;
    }

    /// <summary>重命名文件夹。名字前后的空格会去掉，空名字直接拒绝。</summary>
    public bool RenameFolder(DockItem folder, string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) return false;

        var config = FolderConfig(folder);
        if (config is null || config.Name == name) return false;

        config.Name = name;
        DockStore.Save(_configs);
        return true;
    }

    /// <summary>按文件夹图标取它的配置（返回的是引用，改它就是改持久化数据）。</summary>
    public DockItemConfig? FolderConfig(DockItem folder)
    {
        var index = Items.IndexOf(folder);
        if (index < 0 || index >= _configs.Count) return null;

        var config = _configs[index];
        return config.IsFolder ? config : null;
    }

    private string NextFolderName()
    {
        const string baseName = "新建文件夹";

        if (!_configs.Any(c => c.Name == baseName)) return baseName;

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!_configs.Any(c => c.Name == candidate)) return candidate;
        }

        return baseName;
    }

    // ---------------- 重建 ----------------

    private bool _iconRetryDone;

    private void Rebuild()
    {
        Items.Clear();

        var missing = 0;

        foreach (var config in _configs)
        {
            var item = CreateItem(config);
            if (item is null) continue;

            Items.Add(item);
            if (item.IsApp && item.Icon is null) missing++;
        }

        AppendTail();

        // 图标取不到，多半是「exe 正被自己更新占用 / Shell 缓存还没就绪」这种临时状态，
        // 隔一会儿再取往往就有了。只重试一次：路径真失效时反复重建整行没有意义。
        if (missing > 0 && !_iconRetryDone) ScheduleIconRetry(missing);
    }

    /// <summary>延迟重建一次，专治「启动瞬间图标空白、过一会儿自己好」的情况。</summary>
    private void ScheduleIconRetry(int missing)
    {
        _iconRetryDone = true;
        App.Log($"Dock：{missing} 个图标没取到，1.5 秒后重试", null);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        // 用 DispatcherTimer 而不是 Task.Run：重建会动 ObservableCollection，
        // 必须在 UI 线程上做，省得再套一层 BeginInvoke。
        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(1500),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { },
            dispatcher);

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            var still = 0;
            foreach (var item in Items)
            {
                if (item.IsApp && item.Icon is null) still++;
            }

            if (still > 0) App.Log($"Dock：重试后仍有 {still} 个图标取不到（显示首字兜底）", null);

            Rebuild();
        };

        timer.Start();
    }

    /// <summary>
    /// 尾部：可选的分隔线 + 窗口预览 + 可选的分隔线 + 废纸篓。
    /// 预览区和废纸篓各自前面各有一条分隔线，所以没预览时只有一条。
    /// </summary>
    private void AppendTail()
    {
        if (_previews.Count > 0 || _overflowItem is not null)
        {
            Items.Add(DockItem.CreateSeparator());

            foreach (var preview in _previews) Items.Add(preview);

            // 折叠了才有入口，排在最后一张预览后面
            if (_overflowItem is not null) Items.Add(_overflowItem);

            if (Settings.ShowTrashIcon)
            {
                Items.Add(DockItem.CreateSeparator());
                Items.Add(DockItem.CreateTrash());
            }

            return;
        }

        // 废纸篓关掉时，分隔线也没意义（它本来就是用来隔开应用和废纸篓的）
        if (Settings.ShowTrashIcon)
        {
            Items.Add(DockItem.CreateSeparator());
            Items.Add(DockItem.CreateTrash());
        }
    }

    /// <summary>
    /// 只重建尾部（预览 + 分隔线 + 废纸篓）。窗口开/关时走这条路，
    /// 因为完整 Rebuild 会把所有应用图标重新用 Shell COM 取一遍，代价太大。
    /// </summary>
    private void RebuildTail()
    {
        var keep = _configs.Count;

        while (Items.Count > keep) Items.RemoveAt(Items.Count - 1);

        AppendTail();
    }

    /// <summary>按应用合并后的一个预览组：同一个 exe 的所有窗口共用一张预览。</summary>
    public sealed class WindowGroup
    {
        public WindowGroup(string key, OpenWindow primary)
        {
            Key = key;
            Primary = primary;
            Windows.Add(primary);

            // 应用名取 exe 文件名（"Code.exe" → "Code"），拿不到就退回窗口标题。
            // 菜单里按应用分组显示时用它当分组标题。
            var exe = Path.GetFileNameWithoutExtension(primary.ExePath);
            AppName = string.IsNullOrWhiteSpace(exe)
                ? (string.IsNullOrWhiteSpace(primary.Title) ? "（未知应用）" : primary.Title)
                : exe;
        }

        public string Key { get; }

        /// <summary>菜单里显示的应用名。</summary>
        public string AppName { get; }

        /// <summary>代表窗口：组里最早启动的那个，缩略图 / 右键菜单都认它。</summary>
        public OpenWindow Primary { get; }

        public List<OpenWindow> Windows { get; } = new();
    }

    /// <summary>
    /// 把窗口预览同步成给定的一批，**按应用合并**：同一个 exe 的多个窗口
    /// 合成一张预览（macOS Dock 的做法），不管开多少个窗口，预览格数都以
    /// 「应用数」封顶，Dock 不会被撑爆；合并的预览带角标，点开弹窗口列表。
    /// 集合没变就什么都不做（缩略图由调用方单独刷）。
    /// </summary>
    public bool SyncWindowPreviews(IReadOnlyList<OpenWindow> windows)
    {
        // 设置里关掉了预览区：清空并撤掉那两条分隔线
        if (!Settings.ShowWindowPreviews)
        {
            WindowGroups = Array.Empty<WindowGroup>();

            if (_previews.Count == 0 && _overflowItem is null) return false;

            _previews.Clear();
            _overflowItem = null;
            RebuildTail();
            return true;
        }

        // 分组：exe 读不到的（提权进程偶尔会这样）按句柄单独一组，别误合并
        var groups = new List<WindowGroup>();

        foreach (var window in windows)
        {
            var key = string.IsNullOrEmpty(window.ExePath)
                ? "\0" + window.Handle
                : window.ExePath.ToLowerInvariant();

            var group = groups.Find(g => g.Key == key);
            if (group is null) groups.Add(new WindowGroup(key, window));
            else group.Windows.Add(window);
        }

        WindowGroups = groups;

        // 可见格数是按 Dock 可用宽度算的。放不下时留最后一格给「全部窗口」入口，
        // 其余折叠进去 —— 与其什么都不显示，不如给个明确能点开的去处。
        var slots = Math.Max(1, _previewSlotLimit);
        var hasOverflow = groups.Count > slots;

        var wanted = hasOverflow
            ? groups.Take(slots - 1).ToList()
            : groups.Take(slots).ToList();

        var hiddenApps = groups.Count - wanted.Count;


        // 集合没变的判定：代表窗口一样 + 每组窗口数一样 + 折叠状态一样。
        // 组里开/关一个非代表窗口也会改变窗口数，走重建（预览内容确实变了）。
        var sameSets = _previews.Count == wanted.Count && _overflowItem is null == !hasOverflow;

        if (sameSets)
        {
            for (var i = 0; i < wanted.Count; i++)
            {
                if (_previews[i].WindowHandle != wanted[i].Primary.Handle
                    || _previews[i].WindowCount != wanted[i].Windows.Count)
                {
                    sameSets = false;
                    break;
                }
            }
        }

        if (sameSets)
        {
            // 顺序没变，只把标题同步一下（浏览器切标签页会改标题）。
            // 标题变了不重建 —— 重建会把缩略图一起丢掉，白抓一遍还闪。
            for (var i = 0; i < wanted.Count; i++)
            {
                _previews[i].Name = wanted[i].Primary.Title;
            }

            // 折叠数量可能变了（开了/关了一个应用），角标就地更新，不重建
            if (_overflowItem is not null) _overflowItem.WindowCount = hiddenApps;

            return false;
        }

        _previews.Clear();

        foreach (var group in wanted)
        {
            var primary = group.Primary;

            // 顺手把 exe 图标取出来当兜底：提权窗口（任务管理器）抓不了缩略图，
            // 界面至少显示应用图标，而不是一个空框。
            var icon = string.IsNullOrWhiteSpace(primary.ExePath)
                ? null
                : IconExtractor.FromShortcut(primary.ExePath);

            _previews.Add(DockItem.CreateWindowPreview(
                primary.Title, primary.Handle, icon,
                group.Windows.Count,
                group.Windows.Select(w => (w.Handle, w.Title)).ToList()));
        }

        // 折叠了才给入口。没折叠时不摆一个永远用不上的格子。
        _overflowItem = hasOverflow ? DockItem.CreateWindowList(hiddenApps) : null;

        RebuildTail();
        return true;
    }

    /// <summary>
    /// 重建时**必须**给每个 config 都产出 item（哪怕图标取不到），
    /// 否则 Items 和 _configs 就错位了，按索引删除会删错东西。
    /// </summary>
    private static DockItem? CreateItem(DockItemConfig config)
    {
        // 系统图标也走配置，这样能像普通图标一样拖动排序
        if (!string.IsNullOrEmpty(config.System))
        {
            return config.System switch
            {
                SystemBrand => DockItem.CreateBrand(),
                SystemWindows => DockItem.CreateWindows(),
                SystemLaunchpad => DockItem.CreateLaunchpad(),
                SystemComputer => DockItem.CreateComputer(),
                _ => null
            };
        }

        if (config.IsFolder)
        {
            var children = new List<DockItem>();

            foreach (var child in config.Items ?? new List<DockItemConfig>())
            {
                var item = CreateItem(child.Name, child.Target, requireIcon: false);
                if (item is not null) children.Add(item);
            }

            return DockItem.CreateFolder(config.Name, children);
        }

        return CreateItem(config.Name, config.Target, requireIcon: false);
    }

    /// <summary>设置里改了系统图标显隐之后调用：先对齐配置，再重建整行。</summary>
    public void RefreshStructure()
    {
        if (SyncSystemItems()) DockStore.Save(_configs);
        Rebuild();
    }

    private static DockItem? CreateItem(string name, string path, bool requireIcon = true)
    {
        var icon = IconExtractor.FromShortcut(path);

        // 用户手动添加时，取不到图标就别加进来（加了个看不见的图标很困惑）；
        // 重建时则要给每一项都留位置，保持 Items 与 _configs 一一对应
        if (icon is null && requireIcon) return null;

        var (layout, plateColor) = icon is null
            ? (IconLayout.Fill, Colors.Transparent)
            : IconAnalyzer.Analyze(icon);

        // 铺满模式且没有底板色（整块画）时不垫任何东西，避免多出一圈白边
        var plate = icon is not null && (layout == IconLayout.Inset || plateColor != Colors.Transparent)
            ? new SolidColorBrush(plateColor)
            : null;

        var processName = ShortcutResolver.ResolveProcessName(path);

        return new DockItem(name, path, icon, layout, plate, processName);
    }

    /// <summary>把第 from 个挪到 insertBefore 位置（拖动排序用）。索引是 Items 里的，含两个系统图标。</summary>
    public void Move(int from, int insertBefore)
    {
        var appEnd = AppEnd;

        if (from < 0 || from >= appEnd) return;

        // 落点只能在配置区里（尾部是预览 / 分隔线 / 废纸篓，插不进去）
        insertBefore = Math.Clamp(insertBefore, 0, appEnd);

        // 落在自己身上、或紧挨着自己后面，等于没动
        if (insertBefore == from || insertBefore == from + 1) return;

        var configIndex = from;
        var targetIndex = insertBefore;

        var config = _configs[configIndex];
        _configs.RemoveAt(configIndex);
        if (targetIndex > configIndex) targetIndex--;
        _configs.Insert(targetIndex, config);

        var item = Items[from];
        Items.RemoveAt(from);
        Items.Insert(insertBefore > from ? insertBefore - 1 : insertBefore, item);

        DockStore.Save(_configs);
    }

    /// <summary>刷新每个图标的「运行中 / 当前前台」状态。调用方控制频率，别每帧调。</summary>
    public void RefreshStates()
    {
        if (Items.Count == 0) return;

        HashSet<string> running;
        try
        {
            running = System.Diagnostics.Process.GetProcesses()
                .Select(p => p.ProcessName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return;
        }

        var foreground = WindowQuery.GetForegroundProcessName();

        // 回收站没有独立进程，只能看窗口在不在；每轮查一次，别在循环里重复枚举
        var trashOpen = WindowQuery.IsTrashOpen();
        var trashActive = trashOpen && WindowQuery.IsTrashForeground();

        foreach (var item in Items)
        {
            if (item.IsTrash)
            {
                item.IsRunning = trashOpen;
                item.IsActive = trashActive;
                continue;
            }

            // 品牌图标：设置页开着就算「在跑」
            if (item.IsBrand)
            {
                item.IsRunning = IsSettingsOpen;
                item.IsActive = false;
                continue;
            }

            // 启动台 / Windows / 我的电脑 / 分隔线没有进程，不显示指示条
            if (item.IsPinned)
            {
                item.IsRunning = false;
                item.IsActive = false;
                continue;
            }

            var isRunning = item.ProcessName is not null && running.Contains(item.ProcessName);
            item.IsRunning = isRunning;

            item.IsActive = isRunning
                && foreground is not null
                && string.Equals(item.ProcessName, foreground, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<DockItemConfig> BuildSeed()
    {
        var seeds = AppScanner.Scan(8).ToList();

        if (seeds.Count > 0) return seeds;

        // 极端情况下（开始菜单读不到）给一个能用的兜底
        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        return File.Exists(explorer)
            ? new[] { new DockItemConfig { Name = "文件资源管理器", Target = explorer } }
            : Array.Empty<DockItemConfig>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
