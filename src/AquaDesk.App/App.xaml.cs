using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AquaDesk.Services;
using AquaDesk.Views;
#if VELOPACK
using Velopack;
#endif

namespace AquaDesk;

public partial class App : Application
{
    private const string MutexName = @"Global\AquaDesk.SingleInstance";

    private Mutex? _singleInstance;

    /// <summary>
    /// 本实例是否已经接管了系统状态（任务栏 / 桌面图标）。只有真正跑到应用设置的那份
    /// 实例才置 true；开机自启双触发时被判为「同一份已在跑」而提前退出的实例保持 false，
    /// 退出时就不去动任务栏（否则会把主实例藏起来的任务栏又放出来）。
    /// </summary>
    private static bool _ownsSystemState;

    private TrayIcon? _tray;
    private ClockPillWindow? _clock;
    private MediaSessionService? _media;
    private StatusDockWindow? _statusDock;

    /// <summary>进程意外结束时的痕迹，方便事后定位（%TEMP%\AquaDesk-crash.log）。</summary>
    private static readonly string CrashLogPath =
        Path.Combine(Path.GetTempPath(), "AquaDesk-crash.log");

    /// <summary>
    /// 日志上限。超过就滚到 <c>.1</c>，只留一代。
    ///
    /// 以前是无限追加：布局 / 胶囊位置这些每帧都可能触发的日志在 UI 线程同步写盘，
    /// 常驻几天后文件能涨到几十 MB，而且每条都要开关一次文件。
    /// </summary>
    private const long MaxLogBytes = 512 * 1024;

    private static readonly object LogLock = new();

    /// <summary>上一条日志，用来压掉「同一句话在极短时间内重复」的刷屏。</summary>
    private static string? _lastLine;
    private static DateTime _lastLineAt;

    /// <summary>
    /// <c>--verbose</c>：把布局 / 位置 / 每帧这类高频排查日志也落盘。
    /// 默认关闭 —— 这些信息只在自己调布局时有用，日常留着只会把日志撑大。
    /// </summary>
    internal static bool Verbose { get; private set; }

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            // 崩溃前先把任务栏还回去 —— 否则用户会卡在「没有任务栏」的桌面上
            RestoreSystemState("UI 线程未处理异常");
            Log("UI 线程未处理异常", e.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            RestoreSystemState("后台线程未处理异常");
            Log("后台线程未处理异常", e.ExceptionObject as Exception);
        };

        // 注销 / 关机：进程跟着结束时先把任务栏还回去，下次登录不带着残留状态
        SessionEnding += (_, _) => RestoreSystemState("会话结束");

        // 兜底：Environment.Exit 之类的收尾路径也会走到这里（在 OnExit 之后）
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSystemState("进程退出");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("未观察的任务异常", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// 把「我们对系统做过的改动」还原：任务栏显示出来、清掉隐藏标记。
    ///
    /// 桌面图标**故意不动** —— 那是用户自己的持久设置（正常退出也不还原），
    /// 而且启动时会按系统实际状态回写设置，崩溃时清掉反而会把用户的开关悄悄改掉。
    /// 真要在进程已经死掉之后恢复图标，用 <c>--restore</c>。
    /// </summary>
    private static void RestoreSystemState(string reason)
    {
        // 本实例没接管过系统状态（例如开机自启双触发时被判为「同一份已在跑」而提前
        // 退出的那个实例），就不要去动任务栏 —— 否则会把主实例藏起来的任务栏放出来。
        if (!_ownsSystemState) return;

        try
        {
            TaskbarService.SetVisible(true);
            TaskbarService.ClearMarker();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 恢复系统状态失败（{reason}）：{ex.Message}");
        }
    }

    /// <summary>关键事件与异常，永远落盘。</summary>
    internal static void Log(string where, Exception? ex = null) => Write(where, ex);

    /// <summary>
    /// 高频排查日志（布局、浮层定位、每帧几何）：只有 <c>--verbose</c> 启动时才写。
    /// 换掉原来那些直接调 <see cref="Log"/> 的位置，避免默认情况下每秒写好几次盘。
    /// </summary>
    internal static void Trace(string where) => Write(where, null, onlyWhenVerbose: true);

    private static void Write(string where, Exception? ex, bool onlyWhenVerbose = false)
    {
        if (onlyWhenVerbose && !Verbose) return;

        var body = ex is null ? where : $"{where}\n{ex}";

        lock (LogLock)
        {
            var now = DateTime.Now;

            // 2 秒内同一句话不重复写：布局被连着触发两次时肉眼看到的日志干净很多
            if (body == _lastLine && (now - _lastLineAt).TotalSeconds < 2) return;
            _lastLine = body;
            _lastLineAt = now;

            try
            {
                var info = new FileInfo(CrashLogPath);

                if (info.Exists && info.Length > MaxLogBytes)
                {
                    var old = CrashLogPath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    NetFxShims.Move(CrashLogPath, old);
                }

                File.AppendAllText(CrashLogPath, $"[{now:yyyy-MM-dd HH:mm:ss}] {body}\n");
            }
            catch
            {
                // 连日志都写不了就没办法了
            }
        }
    }

    /// <summary>
    /// 尝试拿单实例锁，拿不到返回 null（不抛异常）。
    /// <paramref name="timeout"/> 为 0 表示只试一次。
    /// </summary>
    private static Mutex? AcquireMutex(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            try
            {
                var mutex = new Mutex(true, MutexName, out var isFirst);
                if (isFirst) return mutex;

                // 探路用的句柄，不持有
                mutex.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                // 同名全局互斥锁被别的账户占着，当作「已存在」
            }

            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(150);
        }
    }

    /// <summary>把已经在跑的 AquaDesk 请走，好让新版本接管。</summary>
    private static void TakeOverRunningInstances()
    {
        var self = NetFxShims.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == self) continue;

                // 打包出来可能叫 AquaDesk.exe，也可能叫 AquaDesk-0.2.exe，按前缀认
                if (!process.ProcessName.StartsWith("AquaDesk", StringComparison.OrdinalIgnoreCase)) continue;

                // 先好好说：关主窗口，让它在 OnExit 里把任务栏还回去
                if (process.CloseMainWindow() && process.WaitForExit(2500)) continue;

                // 用 Kill() 而不是 Kill(entireProcessTree: true)：
                // 后者在「目标进程是调用者的祖先」时会抛
                // "Cannot be used to terminate a process tree containing the calling process"，
                // 而 AquaDesk 自己会拉起提权子进程，父子关系不总是干净的。
                // 我们只需要干掉这一个进程，没必要连坐。
                process.Kill();
                process.WaitForExit(2500);
            }
            catch (Exception ex)
            {
                Log($"接管旧进程失败：{ex.Message}", null);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 是否已经有「同一份 exe」（同一路径）在跑。用于区分两种「已有实例」：
    /// - 同一份（开机自启双触发 / 重复双击同一个 exe）→ 应静默退出，不要接管；
    /// - 另一个版本 / 框架（路径不同）→ 才走接管（升级、跨框架切换）。
    /// </summary>
    private static bool IsSameExeRunning(string? selfPath)
    {
        if (string.IsNullOrWhiteSpace(selfPath)) return false;

        var self = NetFxShims.ProcessId;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == self) continue;
                if (!process.ProcessName.StartsWith("AquaDesk", StringComparison.OrdinalIgnoreCase)) continue;

                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* 读不到就跳过 */ }

                if (!string.IsNullOrEmpty(path)
                    && string.Equals(path, selfPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // 单个进程读不到不影响判断其它进程
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    /// <summary>
    /// 处理 <c>--wifi-on</c> / <c>--wifi-off</c>：这是被主进程用管理员权限拉起来的自己，
    /// 只改一次射频状态就退出，退出码 0 表示成功。
    /// </summary>
    private bool TryRunWifiCommand(string[] args)
    {
        var flag = args.FirstOrDefault(a =>
            a.Equals("--wifi-on", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--wifi-off", StringComparison.OrdinalIgnoreCase));

        if (flag is null) return false;

        var on = flag.EndsWith("on", StringComparison.OrdinalIgnoreCase);

        var elevated = false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 判断不了就按未提权算
        }

        var ok = WifiService.SetRadioWithoutElevation(on);

        Log($"射频开关（{(on ? "开" : "关")}）管理员={elevated} => {(ok ? "成功" : "失败")}", null);
        Shutdown(ok ? 0 : 1);

        return true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
#if VELOPACK
        // Velopack 安装版：让 VelopackApp 处理更新器传来的 --veloapp-* 钩子参数
        // （首次安装 / 更新后重启）。必须在单实例检查之前。便携版不编译这段。
        VelopackApp.Build().Run();
#endif

        // --verbose 打开高频排查日志（布局 / 浮层定位）。默认关，详见 App.Trace
        Verbose = e.Args.Contains("--verbose");

        // 提权子进程模式：只做一次射频开关然后退出。
        // **必须放在单实例检查之前**——否则会被当成「第二个实例」，
        // 把正在跑的主程序接管掉（等于点一下开关就把自己重启了）。
        if (TryRunWifiCommand(e.Args)) return;

        // 救援模式：被强杀 / 蓝屏之后任务栏可能还藏在外面，用这个参数救一次（不需要单实例锁）
        if (e.Args.Contains("--restore"))
        {
            TaskbarService.SetVisible(true);
            TaskbarService.ClearMarker();
            DesktopIconService.SetVisible(true);
            Log("--restore：任务栏与桌面图标已恢复", null);
            Shutdown(0);
            return;
        }

        // 单实例：常驻工具开两份会互相打架
        _singleInstance = AcquireMutex(TimeSpan.Zero);

        if (_singleInstance is null)
        {
            // 已经有一份在跑。
            //
            // 以前这里是直接 Shutdown()，结果踩了个大坑：改完代码重新编译，双击新 exe，
            // 新进程被互斥锁静默挡回去、旧进程还占着屏幕，用户以为「重启过了」，
            // 实际跑的还是旧版本，改了什么都看不到。
            //
            // 关键区分：跑的是「同一份 exe」还是「另一个版本/框架」。
            // - 同一份 exe（开机自启的 Run 键 + 计划任务同时触发，或重复双击）：
            //   直接静默退出，不要接管。否则第二个会把第一个杀掉再接手，用户看到的就是
            //   「自启后过一会自己退出、又重启」。
            // - 不同路径（升级后的新 exe / 跨框架切换）：才接管。
            var selfPath = NetFxShims.ProcessPath;
            if (string.IsNullOrEmpty(selfPath))
            {
                try { selfPath = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
            }

            if (IsSameExeRunning(selfPath))
            {
                Log("检测到同一份 AquaDesk 已在运行，本实例直接退出（不接管）", null);
                Shutdown();
                return;
            }

            // 不同版本接管：把旧进程请走，自己接手。
            // 先 CloseMainWindow 让它走 OnExit 把任务栏还原，不走再强杀。
            Log("检测到不同版本/路径的实例，开始接管", null);
            TakeOverRunningInstances();

            _singleInstance = AcquireMutex(TimeSpan.FromSeconds(6));
            if (_singleInstance is null)
            {
                Log("接管失败（旧进程没退掉），退出", null);
                Shutdown();
                return;
            }
        }

        // 从这里开始本实例是唯一的运行实例，负责在退出时恢复系统状态
        _ownsSystemState = true;

        base.OnStartup(e);

        var settings = SettingsStore.Load();

        // 设置页和右键菜单跟着外观走
        ThemeManager.Apply(settings.Style);

        Log($"启动 {typeof(App).Assembly.GetName().Version} · PID={NetFxShims.ProcessId}");

        // 自启 / 桌面图标以系统实际状态为准（用户可能在任务管理器里关过）
        // 自启以「设置文件」为准，不要反过来用系统状态覆盖它。
        // 原来这里是 `settings.AutoStart = IsEnabled()`：删任务失败时任务还留着，
        // 于是每次启动都把用户的「关」改回「开」—— 就是「关了自启又自己打开」。
        // 桌面图标仍以系统实际状态为准（那个没有副作用）。
        settings.HideDesktopIcons = !DesktopIconService.IsVisible();

        // 上次是异常退出（标记没清）→ 先把任务栏还回去，再按设置重新应用。
        // 覆盖「taskkill /F / 崩溃处理都没来得及跑」的情况：用户下次启动就自动修好。
        if (TaskbarService.HasStaleMarker())
        {
            Log("检测到上次异常退出（任务栏隐藏标记残留），先恢复任务栏", null);
            TaskbarService.SetVisible(true);
        }

        // 任务栏反过来：以设置文件为准主动应用一遍。
        // 原来这里是 `HideTaskbar = !IsVisible()`，而 OnExit 会把任务栏还回去，
        // 于是下次启动读到「可见」就把设置改回 false —— 隐藏任务栏这个开关等于只用一次。
        TaskbarService.SetVisible(!settings.HideTaskbar);

        // 升级换版本号后，注册表里可能还指着旧 exe，顺手改过来
        if (settings.AutoStart)
        {
            AutoStartService.EnsureCurrentPath();
        }
        else
        {
            // 关着的：清掉可能残留的任务 / Run 项。
            // 允许提权 —— 删根目录的计划任务必须有管理员权限，否则会像以前那样
            // 「静默失败 → 任务留着 → 开机又自己启动」。没有残留时不会弹 UAC。
            AutoStartService.Set(false, allowElevated: true);
        }

        var dock = new DockWindow(settings);
        MainWindow = dock;
        dock.Show();

        // 顶部浮动胶囊：平时显示时间，系统里有媒体会话在播时拉长成播放器
        _media = new MediaSessionService();
        _media.Start();

        _clock = new ClockPillWindow(settings, _media);
        _clock.Show();

        // Dock 右侧：电量 / WiFi / 蓝牙 / 音量 / 输入法
        _statusDock = new StatusDockWindow(settings, dock);
        _statusDock.Show();

        // 三个浮层统一登记：前台窗口最大化 / 全屏时一起沉到底层，还原后一起抬回最前。
        // 启动台、文件夹窗口开着时按住不抢置顶，它们自己有让位逻辑。
        OverlayZOrder.Register(dock);
        OverlayZOrder.Register(_clock);
        OverlayZOrder.Register(_statusDock);
        // 菜单开着的时候也别把浮层抬回置顶，否则状态胶囊会盖住刚弹出的菜单
        OverlayZOrder.HoldTopmost = () => dock.IsTemporaryTopmostOpen || dock.IsMenuOpen;

        // --settings：启动后直接把设置页打开。
        // 给「更新完引导用户看设置」和命令行/快捷方式留个入口，省得每次都去点图标。
        if (Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(() => dock.OpenSettingsOnce()),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // 静默检查更新：等启动这股忙劲过去再查，查到什么都只记在
        // UpdateService.PendingUpdate 里，不弹窗、不打扰。设置页打开时自己会读这个值。
        UpdateService.LoadState();

        var updateProbe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        updateProbe.Tick += (_, _) =>
        {
            updateProbe.Stop();
            UpdateService.CheckQuietly();
        };
        updateProbe.Start();

        // --update-demo：用假数据弹一次「有更新」对话框，纯粹为了调版式。
        // 线上发版时本地版本往往已经高于 GitHub 上那个，正常流程走不到这个分支。
        if (Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--update-demo", StringComparison.OrdinalIgnoreCase)))
        {
            Log("--update-demo：进入演示分支", null);
            var demo = new UpdateCheckResult(
                UpdateStatus.Available,
                new Services.UpdateInfo(
                    "0.7.0",
                    "AquaDesk 0.7.0",
                    """
                    ### 新增
                    - 设置 → 关于里加了「检查更新」，有新版会列出更新内容
                    - 安装版支持 **一键下载安装**；便携版提示网址 + `SHA256`，手动下载
                    - 详情见 [Releases](https://github.com/ShiraiYuzen/AquaDesk/releases)

                    ### 修复
                    - 同一语言下换输入法切不动（搜狗 / 讯飞 / 微软拼音的 HKL 是同一个）
                    """,
                    "https://github.com/ShiraiYuzen/AquaDesk/releases",
                    "https://github.com/ShiraiYuzen/AquaDesk/releases/download/v0.7.0/AquaDesk-0.7.0.exe",
                    "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    73_000_000),
                string.Empty);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    new Views.UpdateDialog(demo).ShowDialog();
                }
                catch (Exception ex)
                {
                    Log("--update-demo：ShowDialog 抛异常", ex);
                }
            }),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // 焦点守卫：我们不是前台时不该握着键盘焦点（详见 FocusGuard 注释）
        FocusGuard.Start((uint)NetFxShims.ProcessId);        var focusGuard = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        focusGuard.Tick += (_, _) => FocusGuard.Tick();
        focusGuard.Start();

        // 任务栏隐藏会改工作区，等 Shell 应用完再让 Dock 贴一次底
        var relayout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        relayout.Tick += (_, _) =>
        {
            relayout.Stop();
            dock.RefreshLayout();
        };
        relayout.Start();

        // 通知区域的小图标：双击显示/隐藏，右键有菜单
        _tray = new TrayIcon(
            toggleDock: dock.ToggleVisibility,
            openSettings: dock.ShowSettings,
            exit: Shutdown,
            isDockVisible: () => dock.IsVisible);

        // 排查用：--test-focus=进程名 直接对一个正在跑的应用做一次「点图标」动作
        var focusArg = e.Args.FirstOrDefault(a => a.StartsWith("--test-focus=", StringComparison.Ordinal));
        if (focusArg is not null)
        {
            var processName = focusArg["--test-focus=".Length..];
            var probe = new Models.DockItem(
                processName, string.Empty, null, Services.IconLayout.Fill, null, processName);

            var action = Services.AppWindowController.Toggle(probe);
            Log($"自测聚焦：{processName} → {action}", null);

            // 验证键盘焦点是否真的转移过去了
            var fg = Services.WindowQuery.GetForegroundProcessName();
            Log($"自测聚焦后：前台进程={fg}", null);
        }

        // 排查用：--test-allwindows 直接弹出「全部窗口」菜单（验证菜单的层级和自动关闭）
        if (e.Args.Contains("--test-allwindows"))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                dock.OpenAllWindowsMenuForTest();
            };
            timer.Start();
        }

        // 排查用：--test-scan 打印启动台会扫到哪些应用，用来核对过滤规则有没有误杀
        if (e.Args.Contains("--test-scan"))
        {
            var scanned = Services.AppScanner.Scan(400);
            Log($"扫描结果 {scanned.Count} 个：\n" +
                string.Join("\n", scanned.Select(c => $"  {c.Name}  ←  {c.Target}")), null);
        }

        // 排查用：--media-info 把所有媒体会话写进日志然后退出
        if (e.Args.Contains("--media-info"))
        {
            // 不能 GetResult() 阻塞 UI 线程：WinRT 的异步要回到线程池/UI 线程，
            // 一阻塞就死等（表现是日志里一行都没有）
            _ = MediaSessionService.DumpSessionsAsync();
        }

        // 开发用：带 --settings 启动时直接弹出设置页，方便调参
        if (e.Args.Contains("--settings"))
        {
            dock.ShowSettings();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 只在本实例确实接管过系统状态时才还原；开机自启双触发的「重复实例」没碰过
        // 任务栏，退出时不能去放它（会把主实例藏起来的任务栏放出来）。
        if (_ownsSystemState)
            TaskbarService.SetVisible(true);

        // 谁触发的退出？把调用栈留下来，方便查「自己没了」这类问题
        Log($"正常退出（ExitCode={e.ApplicationExitCode}）\n{Environment.StackTrace}", null);

        _clock?.Close();
        _media?.Dispose();
        _statusDock?.Close();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
