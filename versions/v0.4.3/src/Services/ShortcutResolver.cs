using System.IO;

namespace AquaDesk.Services;

/// <summary>.lnk 解析出来的两样东西：指向哪个 exe、对应哪个进程名。</summary>
public readonly record struct ShortcutInfo(string? TargetPath, string? ProcessName);

/// <summary>
/// 把 .lnk 解析成它指向的可执行文件。
///
/// 走 WScript.Shell 的 COM 接口，比自己实现 IShellLink 省事得多；
/// 解析失败不抛异常，只是判断不了运行状态而已，不该影响 Dock 本身。
/// </summary>
public static class ShortcutResolver
{
    /// <summary>
    /// 系统自带的一些快捷方式指向的是 shell 命名空间（而不是某个 exe），
    /// WScript.Shell 读出来的 TargetPath 是空字符串，只能按名字查表。
    /// </summary>
    private static readonly Dictionary<string, string> KnownProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["File Explorer"] = "explorer",
        ["文件资源管理器"] = "explorer",
        ["Task Manager"] = "Taskmgr",
        ["任务管理器"] = "Taskmgr",
        ["Command Prompt"] = "cmd",
        ["命令提示符"] = "cmd",
        ["Control Panel"] = "explorer",
        ["控制面板"] = "explorer",
        ["Run"] = "explorer",
        ["运行"] = "explorer"
    };

    // 启动台一次要解析几百个快捷方式，COM 调用不便宜，缓存一份
    private static readonly Dictionary<string, ShortcutInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ShortcutInfo Resolve(string path)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }

        var info = ResolveCore(path);

        lock (Cache)
        {
            Cache[path] = info;
        }

        return info;
    }

    /// <summary>返回不带扩展名的进程名（如 "WeChat"），拿不到就返回 null。</summary>
    public static string? ResolveProcessName(string path) => Resolve(path).ProcessName;

    /// <summary>返回快捷方式指向的真实 exe 路径；系统命名空间那种返回 null。</summary>
    public static string? ResolveTargetPath(string path) => Resolve(path).TargetPath;

    /// <summary>
    /// 读快捷方式的图标来源。用来判断 UWP / 商店应用是不是系统自带的 ——
    /// 这类快捷方式没有 TargetPath，但图标会指向 SystemApps 或 WindowsApps 里的包。
    /// </summary>
    public static string? ResolveIconLocation(string path)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(path);

            return shortcut.IconLocation as string;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 读取快捷方式图标失败 {path}: {ex.Message}");
            return null;
        }
    }

    private static ShortcutInfo ResolveCore(string path)
    {
        try
        {
            var extension = Path.GetExtension(path);

            // 直接就是 exe，不用解析
            if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return new ShortcutInfo(path, Path.GetFileNameWithoutExtension(path));
            }

            if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return new ShortcutInfo(null, null);
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return new ShortcutInfo(null, null);

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(path);

            string target = shortcut.TargetPath;

            if (!string.IsNullOrWhiteSpace(target))
            {
                return new ShortcutInfo(target, Path.GetFileNameWithoutExtension(target));
            }

            // TargetPath 为空：先查已知的系统快捷方式
            var name = Path.GetFileNameWithoutExtension(path);
            if (KnownProcessNames.TryGetValue(name, out var known)) return new ShortcutInfo(null, known);

            // 再退一步看图标来源，图标指向某个 exe 的话就用它
            var iconLocation = shortcut.IconLocation as string;
            if (!string.IsNullOrWhiteSpace(iconLocation))
            {
                var iconPath = iconLocation.Split(',')[0].Trim();
                if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return new ShortcutInfo(iconPath, Path.GetFileNameWithoutExtension(iconPath));
                }
            }

            return new ShortcutInfo(null, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 解析快捷方式失败 {path}: {ex.Message}");
            return new ShortcutInfo(null, null);
        }
    }
}
