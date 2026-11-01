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
    // net48 实验构建用独立身份，避免和 .NET 8 主版本共用同一个 Run 值 / 计划任务而互相串扰
    private const string ValueName = "AquaDesk-net48";

    /// <summary>
    /// 判断一个 Run 值名是不是我们自己的。
    /// net48 构建用独立身份 <c>AquaDesk-net48</c>，只认这个名字，
    /// 避免和 .NET 8 主版本（Run 值名 "AquaDesk"）互相误删对方的自启项。
    /// </summary>
    private static bool IsOwnValueName(string name) =>
        name.Contains("AquaDesk-net48", StringComparison.OrdinalIgnoreCase);

    private const string TaskName = "AquaDesk-net48";

    /// <summary>当前可执行文件的路径。单文件发布时指向原始 exe，不是临时解压目录。</summary>
    private static string? ExecutablePath
    {
        get
        {
            var path = NetFxShims.ProcessPath;

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
        "AquaDesk-net48", "bin", "AquaDesk.exe");

    /// <summary>
    /// 把当前 exe 所在目录整目录同步到固定副本目录（exe + 依赖 dll + config 一起拷）。
    /// 只拷单个 exe 会导致从稳定副本启动时因缺 dll 而崩溃——net48 是文件夹部署，不是单文件自包含。
    /// 文件长度和时间都没变就跳过；个别被占用（如正在运行的 exe 自身）的文件跳过，不阻断其余文件。
    /// </summary>
    private static string? SyncStableCopy(string sourcePath)
    {
        try
        {
            var target = StableCopyPath;
            var targetDir = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(targetDir);

            var sourceDir = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
                return target;

            foreach (var srcFile in Directory.GetFiles(sourceDir))
            {
                var name = Path.GetFileName(srcFile);
                var dstFile = Path.Combine(targetDir, name);
                try
                {
                    var srcInfo = new FileInfo(srcFile);
                    var dstInfo = new FileInfo(dstFile);
                    if (dstInfo.Exists
                        && dstInfo.Length == srcInfo.Length
                        && dstInfo.LastWriteTimeUtc >= srcInfo.LastWriteTimeUtc)
                    {
                        continue;
                    }
                    File.Copy(srcFile, dstFile, overwrite: true);
                }
                catch
                {
                    // 个别文件被占用（例如当前正运行的 exe 自身）跳过，不阻断其余文件
                }
            }
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

            // 同步固定副本（任务 / Run 键都指向它，改版本号时只更新副本）
            var launchPath = SyncStableCopy(path) ?? path;

            // 双保险：只要自启开着，就确保 HKCU 的 Run 键也存在做兜底。
            // 计划任务偶尔不触发（普通权限建根目录任务被拒、回落 schtasks 带上一堆保守默认、
            // 笔记本电池策略等），而 Run 键是 Windows 登录启动最稳的入口。
            // 两者指向同一个稳定副本，全局单实例互斥锁会拦掉重复启动，不会真开两个。
            WriteRunValue(launchPath);

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

        // 计划任务优先注册；但不管任务是否建成功，都再写一份 Run 键做兜底——
        // 实测计划任务偶尔不触发（普通权限下建根目录任务会被拒、回落到 schtasks 后
        // 又带上一堆保守默认策略；笔记本电池策略、UAC 回落等都会让它不拉起程序），
        // 而 HKCU 的 Run 键是 Windows 登录时最稳的自启入口。两者指向同一个稳定副本，
        // 全局单实例互斥锁（Global\AquaDesk.SingleInstance）会拦掉重复启动，不会真的开两个。
        var taskOk = RegisterLogonTask(launchPath, allowElevated);
        var runOk = WriteRunValue(launchPath);

        return taskOk || runOk;
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

            // 按名字扫，只认 "AquaDesk-net48"（net48 独立身份，不影响主版本的 "AquaDesk"）
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

            // 把 net48 的 "AquaDesk-net48" 删掉（独立身份，不会误删主版本的 "AquaDesk"）
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
