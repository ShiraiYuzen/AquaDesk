using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AquaDesk.Services;

namespace AquaDesk.Models;

public enum DockItemKind
{
    /// <summary>用户添加的应用。</summary>
    App,

    /// <summary>AquaDesk 自己的图标，固定在最左边。</summary>
    Brand,

    /// <summary>Windows 开始菜单图标，点它弹电源菜单（锁定/睡眠/关机/重启）。</summary>
    Windows,

    /// <summary>启动台，固定在品牌图标右边。</summary>
    Launchpad,

    /// <summary>我的电脑，固定在启动台右边。</summary>
    Computer,

    /// <summary>用户新建的文件夹，里面可以放文件和程序。</summary>
    Folder,

    /// <summary>一个打开中的窗口的预览（点一下切过去）。</summary>
    Window,

    /// <summary>
    /// 预览区末尾的「全部窗口」入口。应用多到可见格数放不下时出现，
    /// 点开是「应用 → 窗口」两级菜单，把被折叠的都捞回来。
    /// </summary>
    WindowList,

    /// <summary>分隔线，把应用和废纸篓分开。</summary>
    Separator,

    /// <summary>废纸篓，固定在最右边。</summary>
    Trash
}

/// <summary>Dock 上的一个图标。缩放/位移由 DockWindow 统一管理，模型只描述「是什么」。</summary>
public sealed class DockItem : INotifyPropertyChanged
{
    private bool _isRunning;
    private bool _isActive;
    private ImageSource? _thumbnail;
    private string _name = string.Empty;

    public DockItem(
        string name,
        string shortcutPath,
        ImageSource? icon,
        IconLayout layout,
        Brush? plateBrush,
        string? processName,
        DockItemKind kind = DockItemKind.App,
        IReadOnlyList<DockItem>? children = null,
        IntPtr windowHandle = default)
    {
        Name = name;
        ShortcutPath = shortcutPath;
        Icon = icon;
        Layout = layout;
        PlateBrush = plateBrush;
        ProcessName = processName;
        Kind = kind;
        Children = children ?? Array.Empty<DockItem>();
        WindowHandle = windowHandle;
        Initial = GetInitial(name);
    }

    /// <summary>
    /// 图标取不到时显示的兜底字符（名字首个字素）。
    /// 正常情况下不该走到这里 —— 但 exe 被占用、商店应用、路径失效都可能让图标为空，
    /// 留一个空格子用户完全看不出那是哪个程序。
    /// </summary>
    public string Initial { get; }

    private static string GetInitial(string name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "?";

        // 取首个「字素簇」而不是首个 char：emoji / 组合字符被劈开会渲染成方块
        return System.Globalization.StringInfo.GetNextTextElement(trimmed).ToUpperInvariant();
    }

    /// <summary>固定在最左边、不参与排序/删除/持久化的两个系统图标。</summary>
    public static DockItem CreateBrand() => new(
        "AquaDesk", string.Empty, null, IconLayout.Fill, null, null, DockItemKind.Brand);

    public static DockItem CreateLaunchpad() => new(
        "启动台", string.Empty, null, IconLayout.Fill, null, null, DockItemKind.Launchpad);

    public static DockItem CreateWindows() => new(
        "Windows 菜单", string.Empty, null, IconLayout.Fill, null, null, DockItemKind.Windows);

    public static DockItem CreateComputer() => new(
        "我的电脑", string.Empty, null, IconLayout.Fill, null, null, DockItemKind.Computer);

    public static DockItem CreateFolder(string name, IReadOnlyList<DockItem> children) => new(
        name, string.Empty, null, IconLayout.Fill, null, null, DockItemKind.Folder, children);

    /// <summary>
    /// 窗口预览：标题即主窗口标题，点一下切换前台 / 最小化。
    /// icon 是抓不到缩略图时的兜底（提权窗口抓不了，总不能给用户一个空白框）。
    /// 同一应用的多个窗口会合并成一个预览：windowCount 是组里的窗口数（&gt;1 时显示角标），
    /// group 是组里每个窗口的句柄和标题（点预览弹出窗口列表）。
    /// </summary>
    public static DockItem CreateWindowPreview(string title, IntPtr hwnd, ImageSource? icon,
        int windowCount = 1, IReadOnlyList<(IntPtr Handle, string Title)>? group = null) => new(
        title, string.Empty, icon, IconLayout.Fill, null, null, DockItemKind.Window,
        windowHandle: hwnd)
    {
        WindowCount = windowCount,
        GroupWindows = group ?? Array.Empty<(IntPtr, string)>()
    };

    public static DockItem CreateSeparator() => new(
        "分隔", string.Empty, null, IconLayout.Fill, null, null, DockItemKind.Separator);

    /// <summary>
    /// 预览区的「全部窗口」入口。hiddenApps 是被折叠、没显示成卡片的应用数，
    /// 画在右上角角标上（所以它 1 也要显示，而普通预览 >1 才显示）。
    /// </summary>
    public static DockItem CreateWindowList(int hiddenApps) => new(
        "全部窗口", string.Empty, null, IconLayout.Fill, null, null, DockItemKind.WindowList)
    {
        WindowCount = hiddenApps
    };

    public static DockItem CreateTrash() => new(
        "废纸篓", string.Empty, null, IconLayout.Fill, null, null, DockItemKind.Trash);

    public DockItemKind Kind { get; }

    public bool IsApp => Kind == DockItemKind.App;

    public bool IsBrand => Kind == DockItemKind.Brand;

    public bool IsLaunchpad => Kind == DockItemKind.Launchpad;

    public bool IsWindows => Kind == DockItemKind.Windows;

    public bool IsComputer => Kind == DockItemKind.Computer;

    public bool IsFolder => Kind == DockItemKind.Folder;

    public bool IsWindowPreview => Kind == DockItemKind.Window;

    public bool IsWindowList => Kind == DockItemKind.WindowList;

    public bool IsSeparator => Kind == DockItemKind.Separator;

    public bool IsTrash => Kind == DockItemKind.Trash;

    /// <summary>系统图标：品牌 / Windows / 启动台 / 我的电脑。它们也排在 dock.json 里，可以拖动排序。</summary>
    public bool IsSystem => Kind is DockItemKind.Brand or DockItemKind.Windows
        or DockItemKind.Launchpad or DockItemKind.Computer;

    /// <summary>图标行尾部：分隔线 / 废纸篓。</summary>
    public bool IsTail => Kind is DockItemKind.Separator or DockItemKind.Trash;

    /// <summary>
    /// 能不能拖动排序。系统图标也能拖（它们现在排在 dock.json 里），
    /// 分隔线和废纸篓钉死在尾部；窗口预览 / 全部窗口入口是临时的，也不该拖。
    /// </summary>
    public bool CanDrag => Kind is not (DockItemKind.Separator or DockItemKind.Trash
        or DockItemKind.Window or DockItemKind.WindowList);

    /// <summary>没有进程可查的项（系统图标 / 分隔线 / 废纸篓 / 窗口预览）。</summary>
    public bool IsPinned => Kind is not (DockItemKind.App or DockItemKind.Folder);

    /// <summary>文件夹里的内容；非文件夹为空。</summary>
    public IReadOnlyList<DockItem> Children { get; }

    /// <summary>窗口预览对应的窗口句柄；其他类型为 0。</summary>
    public IntPtr WindowHandle { get; }

    private int _windowCount = 1;

    /// <summary>这个预览合并了几个窗口（同应用多窗口合并时 &gt; 1，单窗口恒为 1）。</summary>
    public int WindowCount
    {
        get => _windowCount;
        set
        {
            if (_windowCount == value) return;
            _windowCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowWindowCount)));
        }
    }

    /// <summary>要不要显示「合并了 N 个窗口」的角标。</summary>
    /// <summary>
    /// 右上角显示数字。普通预览只在合并了多个窗口时才有意义（=1 没信息量）；
    /// 「全部窗口」入口的数字就是折叠了几个，所以 1 也要显示。
    /// </summary>
    public bool ShowWindowCount => _windowCount > (IsWindowList ? 0 : 1);

    /// <summary>合并组里每个窗口的（句柄, 标题），点预览弹列表用；单窗口预览为空。</summary>
    public IReadOnlyList<(IntPtr Handle, string Title)> GroupWindows { get; set; } =
        Array.Empty<(IntPtr, string)>();

    /// <summary>
    /// 窗口预览的缩略图。这是唯一会在运行时变的东西 —— 每轮刷新换一张图，
    /// 但不重建 item，所以不会让 ItemsControl 重新生成容器（那会闪）。
    /// </summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        }
    }

    /// <summary>是否已经有缩略图；没有时界面显示 Icon 兜底（提权窗口抓不了）。</summary>
    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>文件夹图标上的 2×2 缩略图，最多取前四个。</summary>
    public IReadOnlyList<DockItem> Thumbnails =>
        Children.Count <= 4 ? Children : Children.Take(4).ToList();

    /// <summary>文件夹是否为空（用来决定图标里画不画占位）。</summary>
    public bool IsEmptyFolder => IsFolder && Children.Count == 0;

    /// <summary>
    /// 显示名 / 窗口标题。窗口预览会跟着窗口标题变（浏览器切标签页），
    /// 所以这里做成可写 —— 改名字不该触发整行重建。
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    /// <summary>开始菜单里的 .lnk 路径，点击时交给 Shell 启动。</summary>
    public string ShortcutPath { get; }

    public ImageSource? Icon { get; }

    /// <summary>铺满还是内嵌，见 IconAnalyzer。</summary>
    public IconLayout Layout { get; }

    /// <summary>垫在图标下面的底色；null 表示不垫（图标自己就是整块画）。</summary>
    public Brush? PlateBrush { get; }

    /// <summary>对应的进程名（不带 .exe），用来判断运行状态、关闭应用。可能为 null。</summary>
    public string? ProcessName { get; }

    /// <summary>是否正在运行，由 DockWindow 定时刷新。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
        }
    }

    /// <summary>是否是当前前台窗口所属的应用——是的话运行指示条会变大。</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    /// <summary>铺满模式：图标裁成圆角方形，原尺寸顶满磁贴。</summary>
    public bool Fill => Layout == IconLayout.Fill;

    /// <summary>内嵌模式：图标缩小一圈放在对比色底板上。</summary>
    public bool Inset => Layout == IconLayout.Inset;

    public void Launch()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ShortcutPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 启动失败 {ShortcutPath}: {ex.Message}");
        }
    }

    /// <summary>关掉这个应用的所有进程。</summary>
    public bool Close()
    {
        if (string.IsNullOrEmpty(ProcessName)) return false;

        var closed = false;

        try
        {
            foreach (var process in Process.GetProcessesByName(ProcessName))
            {
                try
                {
                    process.Kill();
                    closed = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AquaDesk] 关闭 {ProcessName} 失败：{ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 枚举 {ProcessName} 失败：{ex.Message}");
        }

        return closed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
