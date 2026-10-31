using System.IO;
using AquaDesk.Models;

namespace AquaDesk.Services;

/// <summary>
/// 扫开始菜单里的快捷方式当 Dock 内容。
/// 比「猜 Program Files 路径」可靠：用户装了什么，开始菜单里就有什么。
/// </summary>
public static class AppScanner
{
    private static readonly string[] NoiseKeywords =
    {
        "uninstall", "卸载", "readme", "说明", "帮助", "help", "manual",
        "文档", "documentation", "website", "网站", "官网", "更新", "update",
        "修复", "repair", "license", "许可",
        "administrative tools", "管理工具", "辅助功能", "accessibility",
        "livecaptions", "narrator", "magnify", "on-screen keyboard", "放大镜", "讲述人",
        // 下面这些不是「应用」，是脚本宿主、运行时、开发工具、后台服务
        "command prompt", "命令提示符", "powershell", "pwsh",
        "git bash", "git cmd", "git gui", "gitk", "release notes", "发布说明",
        "node.js", "npm", "mysql", "虚拟网络", "vmware netcfg",
        "onedrive",
        // 输入法 / 硬件厂商自带的设置工具，不是用户会「打开」的应用
        "皮肤盒子", "设置向导", "输入法管理器", "steps recorder", "步骤记录器",
        // Windows 自带工具和虚拟化套件
        "remote desktop", "远程桌面", "windows media player",
        "vmware workstation", "vmware player",
        // ---- 包管理器 / 运行时 / 框架 / 依赖项：都不是给用户点的应用 ----
        "yarn", "pnpm", "nvm", "anaconda", "miniconda",
        "nodejs", "jdk", "jre", "openjdk", "msys", "mingw", "cygwin",
        "cmake", "gradle", "maven", "nuget", "vcredist", "redistributable", "运行库",
        "framework", "sdk", "windows kits", "microsoft sdks",
        "visual studio installer", "安装程序", "installer", "setup",
        // ---- 后台服务 / 辅助进程 ----
        "service", "服务", "daemon", "helper", "updater", "更新程序", "driver", "驱动",
        // ---- 命令行 / 调试工具 ----
        "console", "终端", "terminal", "shell", "命令行", "debug", "调试",
        "配置工具", "设置工具", "工具集",
        // ---- 系统自带的声音 / 显示 / 驱动面板 ----
        "音量", "杜比", "dolby", "realtek", "声音", "声卡",
        "控制面板", "control panel", "nvidia control", "intel graphics",
        "amd software", "显卡设置", "显示设置",
        // ---- Windows 自带的 UWP 应用（媒体播放器 / Game Bar / Xbox 这一类）----
        // 它们平时没有 .lnk、进不了扫描，这里是兜底：万一某些机器上有快捷方式也不会漏进来
        "媒体播放器", "media player", "game bar", "游戏栏", "xbox",
        "电影和电视", "movies & tv", "照片", "photos", "相机", "camera",
        "计算器", "calculator", "画图", "paint", "记事本", "notepad",
        "录音机", "sound recorder", "便笺", "sticky notes",
        "获取帮助", "get help", "反馈中心", "feedback hub"
    };

    /// <summary>
    /// 这些 exe 是命令行工具 / 运行时 / 后台服务 / 系统工具，不是给用户点的「应用」。
    /// 按可执行文件名精确匹配，避免误伤 GitHub Desktop 这种名字里带 git 的正常应用。
    /// </summary>
    private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        // Git 自带的命令行 / GUI 包装
        "git-bash.exe", "git-cmd.exe", "git-gui.exe", "gitk.exe", "wish.exe", "sh.exe", "bash.exe",
        // Node 运行时
        "node.exe", "npm.cmd", "npx.cmd",
        // MySQL 的命令行与开发工具
        "mysqlsh.exe", "mysql.exe", "mysqlworkbench.exe",
        // 脚本宿主 / 终端
        "powershell.exe", "pwsh.exe", "cmd.exe", "wt.exe", "conhost.exe",
        // 后台服务与虚拟化辅助工具
        "onedrive.exe", "vmnetcfg.exe", "vmware-tray.exe",
        // Windows 自带工具 + 虚拟化套件
        "mstsc.exe", "wmplayer.exe", "vmware.exe", "vmplayer.exe",

        // ---- 语言运行时 / 解释器 ----
        "python.exe", "pythonw.exe", "py.exe", "pyw.exe",
        "java.exe", "javaw.exe", "javac.exe", "jar.exe",
        "dotnet.exe", "msbuild.exe", "nuget.exe",
        "go.exe", "cargo.exe", "rustc.exe",
        "ruby.exe", "perl.exe", "php.exe",

        // ---- 容器 / 子系统 ----
        "docker.exe", "wsl.exe", "wslconfig.exe",

        // ---- 编译工具链 ----
        "cmake.exe", "ninja.exe", "make.exe", "gcc.exe", "g++.exe",
        "clang.exe", "cl.exe", "ld.exe", "mintty.exe", "msys2.exe",

        // ---- 包管理器 ----
        "pip.exe", "yarn.exe", "pnpm.exe", "conda.exe",
        "gradle.exe", "mvn.exe", "ant.exe",

        // ---- 网络 / 系统命令行 ----
        "regedit.exe", "mmc.exe", "eventvwr.exe", "perfmon.exe", "msconfig.exe"
    };

    /// <summary>
    /// 目标 exe 落在这些目录里的一律不算应用 —— 工具链、运行时、SDK 的安装目录。
    /// 比按名字猜准得多：「Docker Desktop」是应用（名字里有 docker），
    /// 但 Program Files\Docker\...\resources\bin\docker.exe 是命令行工具。
    /// </summary>
    private static readonly string[] BlockedPathFragments =
    {
        @"\nodejs\", @"\nvm\", @"\npm\",
        @"\git\", @"\msys", @"\cygwin", @"\mingw",
        @"\python3", @"\python2", @"\anaconda", @"\miniconda",
        @"\java\", @"\jdk", @"\jre\",
        @"\dotnet\", @"\golang\", @"\rust\",
        @"\cmake\", @"\maven\", @"\gradle\",
        @"\microsoft sdks\", @"\windows kits\"
    };

    /// <summary>
    /// 整个目录都不看的：系统工具、辅助功能、启动项。
    /// 这些东西在开始菜单里占位很大，但对 Dock 来说全是噪音。
    /// </summary>
    private static readonly string[] SkippedFolders =
    {
        "accessibility", "辅助功能", "轻松使用", "administrative tools", "管理工具",
        "system tools", "系统工具", "startup", "启动", "windows 附件",
        "windows accessories", "维护", "maintenance"
    };

    public static IReadOnlyList<DockItemConfig> Scan(int limit = 16)
    {
        // 用户级优先，其次全局；同名去重
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DockItemConfig>();

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var lnk in EnumerateShortcuts(root))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (IsNoise(name)) continue;
                if (!IsApplication(lnk)) continue;
                if (!seen.Add(name)) continue;

                result.Add(new DockItemConfig { Name = name, Target = lnk });
                if (result.Count >= limit) return result;
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateShortcuts(string root)
    {
        // 只扫两层：开始菜单结构是「厂商\应用.lnk」，再深基本是子菜单噪音
        var level1 = SafeFiles(root);
        foreach (var f in level1) yield return f;

        foreach (var dir in SafeDirectories(root))
        {
            if (IsSkippedFolder(Path.GetFileName(dir))) continue;
            foreach (var f in SafeFiles(dir)) yield return f;
        }
    }

    private static bool IsSkippedFolder(string name) =>
        SkippedFolders.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir)
                            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    private static bool IsNoise(string name) =>
        NoiseKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 这个快捷方式指向的到底是不是「应用」。
    ///
    /// 开始菜单里混着大量不是应用的东西：命令行工具（Git Bash、PowerShell、MySQL Shell）、
    /// 运行时（node.exe）、后台服务（OneDrive）、虚拟化辅助工具（虚拟网络编辑器）。
    /// 判断分两步：
    /// 1. 目标 exe 的名字在黑名单里 → 不是应用；
    /// 2. 目标 exe 的 PE 头里 Subsystem 是 <c>IMAGE_SUBSYSTEM_WINDOWS_CUI</c>(3) → 控制台程序，不是应用。
    ///
    /// 解析不出来的一律保留——宁可多留一个，也别把真正的应用误杀。
    /// </summary>
    private static bool IsApplication(string shortcutPath)
    {
        var target = ShortcutResolver.ResolveTargetPath(shortcutPath);

        // UWP / 商店应用（以及 shell 命名空间）解析不出 TargetPath。
        // 这里不能一律保留 —— 系统自带的「音量调节」「杜比音效」就是这类。
        // 靠图标来源判断：指向 SystemApps、或 WindowsApps 里 Microsoft.* 包的，
        // 都是系统自带；指向其他厂商包（比如 Spotify、WhatsApp）的照留。
        if (string.IsNullOrWhiteSpace(target))
        {
            return !IsSystemUwpApp(shortcutPath);
        }

        // ① 结构判断：目标必须是 .exe。
        //    .bat / .cmd / .ps1 / .msi / .url / .chm / .txt 这些是脚本、安装包、文档，
        //    不可能是「应用」——这条跟机器无关，到谁的电脑上都成立。
        if (!string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // ② 装在 C:\Windows 下的是系统自带组件（记事本、音量、控制面板那一类），
        //    用户自己装的应用不会往系统目录里塞。这条最省事也最通用。
        if (IsUnderWindowsFolder(target)) return false;

        var fileName = Path.GetFileName(target);

        // ② 名字精确黑名单（已知的命令行 / 运行时 / 系统工具）
        if (BlockedExecutables.Contains(fileName)) return false;

        // ③ 名字后缀黑名单：-cli / console / service / updater / uninstall 这类，
        //    不依赖具体产品名，任何机器上的同类工具都能被抓住。
        if (HasToolSuffix(fileName)) return false;

        // ④ 装在工具链 / 运行时 / SDK 目录里的，一律不是应用
        if (IsInToolchainFolder(target)) return false;

        // ⑤ 控制台程序（PE Subsystem = 3）不是应用。
        //    这条最通用：绝大多数命令行工具、运行时、后台服务都是 CUI。
        return !IsConsoleExecutable(target);
    }

    /// <summary>
    /// 按后缀识别「工具类」exe。不看产品名，所以在别人电脑上同样有效。
    /// </summary>
    private static bool HasToolSuffix(string fileName)
    {
        var name = fileName.ToLowerInvariant();

        if (name.Contains("unins")) return true;   // unins000.exe / uninstall.exe

        string[] suffixes =
        {
            "cli.exe", "-cli.exe", "_cli.exe",
            "console.exe", "-console.exe", "_console.exe",
            "service.exe", "-service.exe", "_service.exe",
            "updater.exe", "-updater.exe", "_updater.exe",
            "daemon.exe", "-daemon.exe",
            "setup.exe", "-setup.exe", "installer.exe", "-installer.exe"
        };

        return suffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));
    }

    /// <summary>
    /// 解析不出 TargetPath 的快捷方式，是不是系统自带的 UWP / 商店应用。
    /// 判断依据是图标来源：系统组件的图标落在 <c>Windows\SystemApps</c> 或
    /// <c>WindowsApps\Microsoft.*</c> 包里；第三方商店应用是 <c>WindowsApps\厂商名.*</c>，不受影响。
    /// </summary>
    private static bool IsSystemUwpApp(string shortcutPath)
    {
        var icon = ShortcutResolver.ResolveIconLocation(shortcutPath);
        if (string.IsNullOrWhiteSpace(icon)) return false;

        return icon.Contains(@"\SystemApps\", StringComparison.OrdinalIgnoreCase)
               || icon.Contains(@"\WindowsApps\Microsoft.", StringComparison.OrdinalIgnoreCase)
               || icon.Contains(@"\Windows\ImmersiveControlPanel\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>目标 exe 是不是装在工具链 / 运行时 / SDK 的目录里。</summary>
    private static bool IsInToolchainFolder(string path) =>
        BlockedPathFragments.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 是不是装在 Windows 系统目录里（System32 / SysWOW64 / SystemApps / WinSxS …）。
    /// 系统自带的小工具和面板都在这里，用户自己装的应用不会往这儿塞。
    /// </summary>
    private static bool IsUnderWindowsFolder(string path)
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windows)) return false;

            return path.StartsWith(
                windows.TrimEnd('\\') + "\\",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读 PE 头的 Subsystem 字段，判断是不是控制台程序。</summary>
    private static bool IsConsoleExecutable(string path)
    {
        const ushort ImageSubsystemWindowsCui = 3;

        try
        {
            if (!File.Exists(path)) return false;

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40) return false;

            stream.Position = 0;
            if (reader.ReadUInt16() != 0x5A4D) return false;          // MZ

            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 24 + 70 > stream.Length) return false;

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return false;       // PE\0\0

            // 可选头从 PE+24 开始，Subsystem 在可选头偏移 68（PE32 / PE32+ 都一样）
            stream.Position = peOffset + 24 + 68;

            return reader.ReadUInt16() == ImageSubsystemWindowsCui;
        }
        catch
        {
            // 读不了（权限、被占用）就当普通应用
            return false;
        }
    }
}
