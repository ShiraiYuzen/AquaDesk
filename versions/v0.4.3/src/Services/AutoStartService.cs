using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AquaDesk.Services;

/// <summary>
/// 开机自启。
///
/// **优先用「计划任务」，Run 键只作兜底。**
/// 原因：HKCU\...\Run 里的项是 Explorer 加载完桌面之后才逐个启动的，
/// 壁纸软件、各种开机小工具都在这个队列里，谁先谁后基本看运气。
/// 而计划任务的登录触发器由任务计划服务直接拉起，通常早于 Explorer 处理 Run 项，
/// 所以能把 Dock 抢在壁纸软件前面挂上去。
///
/// 任务用「交互式令牌 + 普通权限」注册（TASK_LOGON_INTERACTIVE_TOKEN / LUA）：
/// - 不需要管理员，也不会弹 UAC；
/// - **不能勾「以最高权限运行」**——那样进程会被提权，
///   而 Windows 的 UIPI 会禁止从资源管理器往提权窗口拖文件，Dock 的拖放加图标就废了。
///
/// 任务优先级设 4（默认 7，数字越小越高），进一步抢在普通启动项前面。
/// </summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 注意：**不要指望改名字来插队**。注册表的 Run 值枚举顺序不是按名字排序的
    /// （实测加 "!" 前缀反而排到最后），所以前缀那套在这儿没用。
    /// 想真正排到壁纸软件前面，只能走计划任务。
    /// </summary>
    private const string ValueName = "AquaDesk";

    /// <summary>
    /// 判断一个 Run 值名是不是我们自己的。
    ///
    /// **不能只比对 "AquaDesk"**：历史上试过用 "!AquaDesk" 前缀插队（结论是没用，
    /// 加感叹号反而排到最后），名字后来改回 "AquaDesk"，旧值却没人清 —— 于是出现
    /// 过「设置里关了自启、程序也认为自己没开（读不到 "AquaDesk"），Windows 每次开机
    /// 照样按 "!AquaDesk" 把程序拉起来」。
    /// 现在按「名字里含 aquadesk」匹配，任何历史变体都算自己的，能被读到也能被删掉。
    /// </summary>
    private static bool IsOwnValueName(string name) =>
        name.Contains("AquaDesk", StringComparison.OrdinalIgnoreCase);

    private const string TaskName = "AquaDesk";

    /// <summary>当前可执行文件的路径。单文件发布时指向原始 exe，不是临时解压目录。</summary>
    private static string? ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;

            if (string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    path = Process.GetCurrentProcess().MainModule?.FileName;
                }
                catch
                {
                    return null;
                }
            }

            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
    }

    /// <summary>
    /// 计划任务指向的**固定副本**路径。
    ///
    /// 为什么不让任务直接指向 dist 里那个带版本号的 exe：一改版本号路径就变了，
    /// 任务还指着旧文件（甚至已经被归档删掉），开机自启就失效了。
    /// 指向一个固定路径，升级时只更新这个副本，任务本身不用动，也不用再弹 UAC。
    /// </summary>
    private static string StableCopyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AquaDesk", "bin", "AquaDesk.exe");

    /// <summary>把当前 exe 同步到固定副本（长度和时间都没变就跳过）。</summary>
    private static string? SyncStableCopy(string sourcePath)
    {
        try
        {
            var target = StableCopyPath;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var source = new FileInfo(sourcePath);
            var copy = new FileInfo(target);

            if (copy.Exists
                && copy.Length == source.Length
                && copy.LastWriteTimeUtc >= source.LastWriteTimeUtc)
            {
                return target;
            }

            File.Copy(sourcePath, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 同步自启副本失败：{ex.Message}");
            return null;
        }
    }

    public static bool IsEnabled() => IsTaskRegistered() || HasRunValue();

    /// <summary>
    /// 已经开了自启、但注册里指向的是旧版本的 exe（比如从 0.3.1 升到 0.3.2）时，
    /// 静默改成当前路径。不然升级完开机启动的还是旧文件。
    /// </summary>
    public static void EnsureCurrentPath()
    {
        try
        {
            if (!IsEnabled()) return;

            var path = ExecutablePath;
            if (path is null) return;

            // 只同步固定副本，不重注册任务 —— 否则改个版本号就要弹一次 UAC。
            SyncStableCopy(path);

            // 任务还没建：走 Run 键（不提权，弹 UAC 太烦）
            if (!IsTaskRegistered())
            {
                Set(true, allowElevated: false);
                return;
            }

            // 任务建好了但还指着旧路径（比如升级前注册的）：重注册一次，
            // 这一步要提权，所以只在路径确实不一致时做，正常情况下开机不会弹。
            var current = TaskActionPath();
            if (!string.IsNullOrEmpty(current)
                && !string.Equals(current, StableCopyPath, StringComparison.OrdinalIgnoreCase))
            {
                Set(true, allowElevated: true);
            }
        }
        catch
        {
            // 改不了就算了，用户可以在设置页重新勾一次
        }
    }

    /// <param name="allowElevated">
    /// 允许弹 UAC 提权去注册计划任务。只有用户在设置页手动开关时才给 true；
    /// 开机时自动校正路径不给，免得每次开机都弹一次。
    /// </param>
    public static bool Set(bool enabled, bool allowElevated = true)
    {
        if (!enabled)
        {
            var removed = RemoveTask(allowElevated);
            RemoveRunValue();

            // 删不掉就别骗用户：设置保持「开」，免得界面显示关了、实际还在自启
            if (!removed) App.Log("自启：任务没删掉，仍会在开机时启动", null);

            return removed;
        }

        var path = ExecutablePath;
        if (path is null) return false;

        // 任务和 Run 键都指向固定副本，改版本号时只更新副本就行
        var launchPath = SyncStableCopy(path) ?? path;

        // 计划任务优先；注册成功就把 Run 键删掉，免得开机启动两次
        if (RegisterLogonTask(launchPath, allowElevated))
        {
            RemoveRunValue();
            return true;
        }

        // 计划任务用不了（被策略限制之类）就退回 Run 键
        return WriteRunValue(launchPath);
    }

    // ---------------- 计划任务 ----------------

    private static bool RegisterLogonTask(string exePath, bool allowElevated)
    {
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null) return false;

            dynamic service = Activator.CreateInstance(serviceType)!;
            service.Connect();

            dynamic root = service.GetFolder("\\");
            dynamic task = service.NewTask(0);

            task.RegistrationInfo.Description = "AquaDesk 开机自启（早于 Run 项启动）";

            // 9 = TASK_TRIGGER_LOGON
            dynamic trigger = task.Triggers.Create(9);
            trigger.Enabled = true;

            // 0 = TASK_ACTION_EXEC
            dynamic action = task.Actions.Create(0);
            action.Path = exePath;

            dynamic settings = task.Settings;
            settings.Enabled = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";
            settings.StartWhenAvailable = true;
            settings.Priority = 4;

            // 3 = TASK_LOGON_INTERACTIVE_TOKEN（登录时用当前用户的交互式令牌，不需要密码）
            // 0 = TASK_RUNLEVEL_LUA（普通权限，不提权）
            dynamic principal = task.Principal;
            principal.LogonType = 3;
            principal.RunLevel = 0;

            // 6 = TASK_CREATE_OR_UPDATE，3 = TASK_LOGON_INTERACTIVE_TOKEN
            root.RegisterTaskDefinition(TaskName, task, 6, null, null, 3);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 普通权限下根任务文件夹是只读的，会直接 E_ACCESSDENIED。
            // 退回「提权调 schtasks」——会弹一次 UAC，用户同意后任务才建得起来。
            return allowElevated && RegisterLogonTaskElevated(exePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 注册计划任务失败：{ex.Message}");
            App.Log($"自启：注册计划任务失败 {ex.GetType().Name}: {ex.Message}", null);
            return false;
        }
    }

    /// <summary>
    /// 普通权限建不了计划任务（根任务文件夹只读），退回提权调 schtasks。
    /// **会弹一次 UAC**，用户同意后任务才建得起来；拒绝则返回 false，调用方回落到 Run 键。
    /// </summary>
    private static bool RegisterLogonTaskElevated(string exePath)
    {
        try
        {
            var arguments =
                $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL LIMITED /F";

            var startInfo = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            process.WaitForExit(15000);
            var ok = process.HasExited && process.ExitCode == 0;

            App.Log($"自启：提权注册计划任务 {(ok ? "成功" : "失败")}", null);
            return ok;
        }
        catch (Exception ex)
        {
            // 用户点了 UAC 的「否」也会走到这里
            App.Log($"自启：提权注册被拒绝或失败 {ex.GetType().Name}: {ex.Message}", null);
            return false;
        }
    }

    /// <summary>读计划任务里配置的 exe 路径；读不到返回 null。</summary>
    private static string? TaskActionPath()
    {
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null) return null;

            dynamic service = Activator.CreateInstance(serviceType)!;
            service.Connect();

            dynamic root = service.GetFolder("\\");
            dynamic task = root.GetTask(TaskName);

            return task.Definition.Actions.Item(1).Path as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTaskRegistered()
    {
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null) return false;

            dynamic service = Activator.CreateInstance(serviceType)!;
            service.Connect();

            dynamic root = service.GetFolder("\\");
            root.GetTask(TaskName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RemoveTask(bool allowElevated)
    {
        if (!IsTaskRegistered()) return true;

        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null) return false;

            dynamic service = Activator.CreateInstance(serviceType)!;
            service.Connect();

            dynamic root = service.GetFolder("\\");
            root.DeleteTask(TaskName, 0);

            return !IsTaskRegistered();
        }
        catch (UnauthorizedAccessException)
        {
            // **删任务和建任务一样，普通权限会被拒**（根任务文件夹只读）。
            // 以前这里 catch 掉就算了，结果任务一直留着：用户关了自启，下次开机它照样启动，
            // 启动时又把设置读回 true —— 就是「关了自启又自己打开」。
            return allowElevated && RemoveTaskElevated();
        }
        catch (Exception ex)
        {
            App.Log($"自启：删除计划任务失败 {ex.GetType().Name}: {ex.Message}", null);
            return false;
        }
    }

    /// <summary>提权删除计划任务（会弹一次 UAC）。</summary>
    private static bool RemoveTaskElevated()
    {
        try
        {
            var startInfo = new ProcessStartInfo(
                "schtasks.exe", $"/Delete /TN \"{TaskName}\" /F")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            process.WaitForExit(15000);
            var ok = process.HasExited && process.ExitCode == 0 && !IsTaskRegistered();

            App.Log($"自启：提权删除计划任务 {(ok ? "成功" : "失败")}", null);
            return ok;
        }
        catch (Exception ex)
        {
            App.Log($"自启：提权删除被拒绝或失败 {ex.GetType().Name}: {ex.Message}", null);
            return false;
        }
    }

    // ---------------- Run 键（兜底） ----------------

    private static bool HasRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key is null) return false;

            // 按名字扫，不要写死 "AquaDesk" —— 历史遗留的 "!AquaDesk" 也要算数
            foreach (var name in key.GetValueNames())
            {
                if (!IsOwnValueName(name)) continue;
                if (key.GetValue(name) is string value && value.Length > 0) return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool WriteRunValue(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return false;

            key.SetValue(ValueName, $"\"{exePath}\"");

            // 写完之后把别的变体（比如历史上的 "!AquaDesk"）清掉，免得开机启动两次
            foreach (var name in key.GetValueNames())
            {
                if (string.Equals(name, ValueName, StringComparison.Ordinal)) continue;
                if (!IsOwnValueName(name)) continue;

                key.DeleteValue(name, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 写 Run 键失败：{ex.Message}");
            return false;
        }
    }

    private static void RemoveRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            // 把所有变体都删掉（"AquaDesk" / "!AquaDesk" / 任何含 aquadesk 的名字）
            foreach (var name in key.GetValueNames())
            {
                if (!IsOwnValueName(name)) continue;

                try
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
                catch (Exception ex)
                {
                    App.Log($"自启：删除 Run 值 {name} 失败 {ex.GetType().Name}: {ex.Message}", null);
                }
            }
        }
        catch
        {
            // 忽略
        }
    }
}
