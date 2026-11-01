using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AquaDesk.Core;
using Microsoft.Win32;

namespace AquaDesk.Services;

/// <summary>一个「在后台待着」的程序：要么有最小化 / 被盖住的窗口，要么只活在托盘里。</summary>
public sealed record BackgroundApp(
    uint Pid,
    string Name,
    string Path,
    string Detail,
    IntPtr Window,
    bool Minimized,
    bool TrayOnly,
    bool Foreground,
    ImageSource? Icon);

/// <summary>
/// 后台程序清单。
///
/// Windows **没有**「枚举别人托盘图标」的公开接口 —— 托盘挂在任务栏的 XAML 岛上，
/// 任务栏一藏起来，它的 UI Automation 树就是空的（0.3.7 实测过，见 README）。
/// 所以换个角度，把托盘真正承担的两件事拆开做：
///
/// 1. **切回程序**：枚举最小化 / 被盖住的顶层窗口，点一下还原并置前。纯 Win32，最可靠。
/// 2. **认出「谁在托盘里」**：读 <c>HKCU\Control Panel\NotifyIconSettings</c>，
///    每个图标都存着 <c>ExecutablePath</c>、<c>InitialTooltip</c> 和一张 PNG 快照
///    （<c>IconSnapshot</c>）。拿 exe 路径去匹配正在运行的进程，就知道哪些进程是托盘程序，
///    连它当年那个托盘图标都能原样画出来。
///
/// 这类程序没有窗口可发 <c>WM_CLOSE</c>，所以「退出」只能结束进程 —— 界面上做成两步确认。
/// </summary>
public static class BackgroundApps
{
    public static uint SelfPid => (uint)NetFxShims.ProcessId;

    /// <summary>扫一遍：先窗口，后托盘。</summary>
    public static List<BackgroundApp> Query()
    {
        // 让两半共用一份「有窗口的进程名」：窗口区按 pid 去重，托盘区靠它避免同一程序出现两次
        var windowNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var windows = CollectWindows(windowNames);
        var trayOnly = CollectTrayOnly(windowNames);

        var list = new List<BackgroundApp>(windows.Count + trayOnly.Count);

        // 最小化的排前面（这是最想点的那批），其次被盖住的窗口，最后是托盘程序
        list.AddRange(windows
            .OrderByDescending(w => w.Minimized)
            .ThenByDescending(w => w.Foreground)
            .ThenBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase));

        list.AddRange(trayOnly);
        return list;
    }

    // ---------------- 枚举窗口 ----------------

    private static List<BackgroundApp> CollectWindows(HashSet<string> windowNames)
    {
        var byPid = new Dictionary<uint, BackgroundApp>();
        var foreground = NativeMethods.GetForegroundWindow();

        // 前台那个应用不算「后台」，但它确实有窗口 —— 名字要记下来，
        // 否则多进程程序（Electron 那类）会从托盘那条路再冒出来一次
        if (foreground != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);

            var foregroundPath = TryGetProcessPath(foregroundPid);
            if (foregroundPath is not null) windowNames.Add(Path.GetFileName(foregroundPath));
        }

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                Consider(hwnd, foreground, byPid, windowNames);
            }
            catch
            {
                // 枚举途中窗口随时会被销毁，单条失败不影响整张表
            }

            return true;
        }, IntPtr.Zero);

        return byPid.Values.ToList();
    }

    private static void Consider(
        IntPtr hwnd, IntPtr foreground,
        Dictionary<uint, BackgroundApp> byPid, HashSet<string> windowNames)
    {
        // 前台那个窗口也算进来，只是标成「当前前台」。
        // 早先是直接 return 掉的 —— 那时以为「前台 ≠ 后台」，但胶囊是 WS_EX_NOACTIVATE，
        // 点它不会抢焦点，用户刚用过的程序会一直是前台：结果就是那个程序从列表里彻底消失
        // （0.4.4 实测：同时开着 WorkBuddy 和 WorkBuddy AI，只列出最小化的后者）。
        var isForeground = hwnd == foreground;

        if (!NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd)) return;

        if (NativeMethods.GetWindowTextLengthW(hwnd) == 0) return;

        // 工具窗口（输入法候选框、浮动提示）不是「程序」
        var exStyle = NativeMethods.GetExtendedStyle(hwnd);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0
            && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0) return;

        // 有 owner 的（模态对话框、贴在主窗口边上的浮动面板）不算独立程序
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return;

        // UWP 会先把窗口建好再 cloak 起来，这些不算
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
            && cloaked != 0) return;

        // 消息窗口（「DWM Notification Window」「Listen Window」这种）没有标题栏，不是应用
        if ((NativeMethods.GetStyle(hwnd) & NativeMethods.WS_CAPTION) != NativeMethods.WS_CAPTION) return;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == SelfPid) return;

        var title = new StringBuilder(260);
        NativeMethods.GetWindowTextW(hwnd, title, title.Capacity);

        var minimized = NativeMethods.IsIconic(hwnd);

        // 拿不到 exe 路径：系统进程或提权进程，列出来也没法操作，直接跳过
        var path = TryGetProcessPath(pid);
        if (path is null) return;

        var fileName = Path.GetFileName(path);
        windowNames.Add(fileName);

        var name = Path.GetFileNameWithoutExtension(path);

        // 一个进程可能有多个窗口：优先留最小化的那个；但前台那个更值得留，别被最小化的兄弟窗口顶掉
        if (byPid.TryGetValue(pid, out var existing) && existing.Minimized && !minimized && !isForeground) return;

        byPid[pid] = new BackgroundApp(
            pid,
            name,
            path,
            title.ToString(),
            hwnd,
            minimized,
            TrayOnly: false,
            Foreground: isForeground,
            Icon: null);
    }

    // ---------------- 托盘登记表 ----------------

    private sealed record TrayEntry(string Path, string Tooltip, byte[]? Snapshot);

    private static List<BackgroundApp> CollectTrayOnly(HashSet<string> windowNames)
    {
        var result = new List<BackgroundApp>();

        var entries = ReadTrayEntries();
        if (entries.Count == 0) return result;

        var byName = new Dictionary<string, List<TrayEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var file = Path.GetFileName(entry.Path);
            if (file.Length == 0) continue;

            if (!byName.TryGetValue(file, out var list))
            {
                list = new List<TrayEntry>();
                byName[file] = list;
            }

            list.Add(entry);
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return result;
        }

        // 按 exe 路径聚合：Electron / Chromium 那类一个程序能开出十几个进程，
        // 一个进程一行会把面板刷成一堆重名条目
        var best = new Dictionary<string, (uint Pid, DateTime Started, TrayEntry Entry)>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in processes)
        {
            try
            {
                if ((uint)process.Id == SelfPid) continue;

                // 先按进程名粗筛，别给几百个进程都去查一次全路径
                if (!byName.TryGetValue(process.ProcessName + ".exe", out var candidates)) continue;

                var path = TryGetProcessPath((uint)process.Id);
                if (path is null) continue;
                if (windowNames.Contains(Path.GetFileName(path))) continue;

                var entry = candidates.FirstOrDefault(c =>
                    string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));

                if (entry is null) continue;

                // 同一个 exe 只留启动最早的那个（一般是主进程，结束它整组子进程也会跟着退）
                DateTime started;
                try
                {
                    started = process.StartTime;
                }
                catch
                {
                    started = DateTime.MaxValue;
                }

                if (best.TryGetValue(path, out var current) && current.Started <= started) continue;

                best[path] = ((uint)process.Id, started, entry);
            }
            catch
            {
                // 进程随时可能退出
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var (path, info) in best)
        {
            result.Add(new BackgroundApp(
                info.Pid,
                Path.GetFileNameWithoutExtension(path),
                path,
                string.IsNullOrWhiteSpace(info.Entry.Tooltip) ? "在托盘里运行" : info.Entry.Tooltip,
                IntPtr.Zero,
                Minimized: false,
                TrayOnly: true,
                Foreground: false,
                Icon: DecodeSnapshot(info.Entry.Snapshot)));
        }

        return result
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<TrayEntry> ReadTrayEntries()
    {
        var list = new List<TrayEntry>();

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root is null) return list;

            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key is null) continue;

                    if (key.GetValue("ExecutablePath") is not string path) continue;
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    var tooltip = key.GetValue("InitialTooltip") as string ?? string.Empty;
                    var snapshot = key.GetValue("IconSnapshot") as byte[];

                    if (!File.Exists(path)) continue;

                    // 自己的图标：直接跳过。以前用 dotnet 宿主跑过，注册的 exe 是 dotnet.exe，
                    // 只比进程 pid 拦不住，会把 AquaDesk 自己列成「托盘程序」
                    if (string.Equals(path, NetFxShims.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (tooltip.Contains("AquaDesk", StringComparison.OrdinalIgnoreCase)) continue;

                    list.Add(new TrayEntry(path, tooltip, snapshot));
                }
                catch
                {
                    // 单个键读坏了就算了
                }
            }
        }
        catch
        {
            // 老系统没这个键：只显示窗口程序
        }

        return list;
    }

    /// <summary>托盘图标快照是 PNG，直接解出来用 —— 比拿 exe 重新抠一遍更还原。</summary>
    private static ImageSource? DecodeSnapshot(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            using var stream = new MemoryStream(bytes);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- 进程路径 ----------------

    /// <summary>取进程的可执行文件全路径。提权进程拿不到时返回 null（不抛）。</summary>
    public static string? TryGetProcessPath(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;

            return NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    // ---------------- 操作 ----------------

    /// <summary>还原（如果是最小化的）并切到前台。</summary>
    public static bool Activate(BackgroundApp app)
    {
        if (app.Window == IntPtr.Zero) return false;

        if (app.Minimized)
        {
            NativeMethods.ShowWindow(app.Window, NativeMethods.SW_RESTORE);
        }

        return WindowActivator.Activate(app.Window);
    }

    /// <summary>结束进程。托盘程序没有窗口，只能这么退。</summary>
    public static bool End(BackgroundApp app)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_TERMINATE, false, app.Pid);
        if (handle == IntPtr.Zero) return false;

        try
        {
            return NativeMethods.TerminateProcess(handle, 0);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>在资源管理器里选中这个 exe。</summary>
    public static void RevealInExplorer(BackgroundApp app)
    {
        if (app.Path.Length == 0) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{app.Path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.Log("后台程序：定位文件失败", ex);
        }
    }
}
