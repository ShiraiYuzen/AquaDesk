using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AquaDesk.Models;
using AquaDesk.Services;

namespace AquaDesk.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    /// <summary>打开之后改过东西没有。没改过就不出现「保存」按钮。</summary>
    private bool _dirty;

    private System.Windows.Threading.DispatcherTimer? _savedHintTimer;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = settings;

        FillAbout();

#if VELOPACK
        // 安装版才显示「打开安装目录 / 卸载」；便携版没有安装目录，整块隐藏
        InstallOnlySection.Visibility = Visibility.Visible;
#endif

        // 任何一项改动都把「保存」按钮亮出来（改动本身是实时生效的，这里只管写盘）
        _settings.PropertyChanged += OnAnySettingChanged;
        Closing += OnWindowClosing;

        // Esc 关窗：系统标题栏也有 ×，但调参数时手一般还在键盘上
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        // 小屏 / 4:3 屏上 880×668 可能装不下，先按工作区收一下再居中
        Loaded += (_, _) => CenterAt(Width, Height);

        // 窗口有最大尺寸（卡片横着铺满 1920 屏会很难看）。
        // 但系统「最大化」只看显示器尺寸，压到最大尺寸后会把窗口钉在 -8,-8 上，
        // 标题栏被顶出屏幕。所以这里把「最大化」改成「放大到最大尺寸并居中」。
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Maximized) return;

            WindowState = WindowState.Normal;
            CenterAt(MaxWidth, MaxHeight);
        };
    }

    /// <summary>按给定尺寸摆到当前屏幕工作区正中（尺寸超出工作区时先收进来）。</summary>
    private void CenterAt(double width, double height)
    {
        var area = SystemParameters.WorkArea;

        Width = Math.Min(width, area.Width - 40);
        Height = Math.Min(height, area.Height - 40);

        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    /// <summary>「关于」页的三行信息：版本、运行时、配置目录。</summary>
    private void FillAbout()
    {
        // 版本读 InformationalVersion（0.6.9），不是 AssemblyVersion（0.4.12.0）。
        // 后者是给 Win32 文件版本留的四段数字，一直没跟着发版改，直接显示会差一大截。
        VersionValue.Text = UpdateService.CurrentVersionText;

        RuntimeValue.Text = $".NET {Environment.Version}";
        ConfigPathValue.Text = SettingsStore.RootDirectory;

        // 后台静默检查已经发现新版：把按钮文字直接改成「有新版本 x.y.z」，
        // 不用用户先点一次才知道。
        if (UpdateService.PendingUpdate is { } pending)
        {
            CheckUpdateButton.Content = $"有新版本 {pending.Version}";
        }
    }

    /// <summary>
    /// 检查更新：按钮先禁用，回来再恢复。用 async void 是因为它是事件处理器，
    /// 里面所有异常都由 UpdateService 兜住（网络、超时、解析都转成 Failed 结果）。
    /// </summary>
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (!CheckUpdateButton.IsEnabled) return;

        var label = CheckUpdateButton.Content;

        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "检查中…";

        try
        {
            var result = await UpdateService.CheckAsync();

            App.Log($"检查更新：结果={result.Status}，准备弹窗", null);

            var dialog = new UpdateDialog(result) { Owner = this };
            dialog.ShowDialog();

            App.Log("检查更新：对话框已关闭", null);
        }
        catch (Exception ex)
        {
            App.Log("设置页：检查更新异常", ex);
        }
        finally
        {
            CheckUpdateButton.Content = label;
            CheckUpdateButton.IsEnabled = true;
        }
    }


    private void OnStyleClassic(object sender, RoutedEventArgs e) => _settings.Style = DockStyle.Classic;

    private void OnStyleLiquidClear(object sender, RoutedEventArgs e) => _settings.Style = DockStyle.LiquidClear;

    /// <summary>
    /// 恢复默认：**只重置当前这一页**的项。
    /// 以前是一把梭全恢复，在「Dock」页点一下会把外观、系统集成一起改掉，
    /// 用户根本不知道动了什么。
    /// </summary>
    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        // 逐个属性写回，保证绑定链路不被打断（换实例的话 DataContext 就脱钩了）
        var defaults = new AppSettings();

        switch (RootTabs.SelectedIndex)
        {
            case 0: // 外观
                _settings.AllowStyleSwitch = defaults.AllowStyleSwitch;
                _settings.Style = defaults.Style;
                _settings.GlassOpacity = defaults.GlassOpacity;
                _settings.CornerRadius = defaults.CornerRadius;
                break;

            case 1: // Dock
                _settings.UiScale = defaults.UiScale;
                _settings.IconSize = defaults.IconSize;
                _settings.Edge = defaults.Edge;
                _settings.IconGap = defaults.IconGap;
                _settings.BottomMargin = defaults.BottomMargin;
                _settings.MagnifyMaxScale = defaults.MagnifyMaxScale;
                break;

            case 2: // 图标
                _settings.ShowBrandIcon = defaults.ShowBrandIcon;
                _settings.ShowWindowsIcon = defaults.ShowWindowsIcon;
                _settings.ShowLaunchpadIcon = defaults.ShowLaunchpadIcon;
                _settings.ShowComputerIcon = defaults.ShowComputerIcon;
                _settings.ShowTrashIcon = defaults.ShowTrashIcon;
                _settings.ShowWindowPreviews = defaults.ShowWindowPreviews;
                _settings.PreviewIconOnly = defaults.PreviewIconOnly;
                _settings.TopMode = defaults.TopMode;
                _settings.ShowBackgroundAppsButton = defaults.ShowBackgroundAppsButton;
                _settings.ShowBatteryPercent = defaults.ShowBatteryPercent;
                break;

            case 3: // 系统
                _settings.HideTaskbar = defaults.HideTaskbar;
                _settings.HideDesktopIcons = defaults.HideDesktopIcons;
                _settings.PauseMode = defaults.PauseMode;
                _settings.ShowIsland = defaults.ShowIsland;
                _settings.IslandFixed = defaults.IslandFixed;
                _settings.IslandWidth = defaults.IslandWidth;
                _settings.IslandHeight = defaults.IslandHeight;
                _settings.IslandPlayerWidth = defaults.IslandPlayerWidth;
                _settings.IslandPlayerHeight = defaults.IslandPlayerHeight;
                _settings.IslandX = defaults.IslandX;
                _settings.IslandY = defaults.IslandY;
                break;

            // case 4（关于）没有可恢复的项
        }
    }

    /// <summary>全部重置。开机自启不动 —— 它会在系统里留下计划任务 / Run 项，不该被一次误点清掉。</summary>
    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "把外观、Dock、图标、系统四类设置都恢复成默认值？\n\n" +
            "开机自启保持不变，Dock 上的图标和启动台布局也不会被清空。",
            "恢复全部设置",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        var defaults = new AppSettings();

        _settings.AllowStyleSwitch = defaults.AllowStyleSwitch;
        _settings.Style = defaults.Style;
        _settings.GlassOpacity = defaults.GlassOpacity;
        _settings.CornerRadius = defaults.CornerRadius;

        _settings.UiScale = defaults.UiScale;
        _settings.IconSize = defaults.IconSize;
        _settings.Edge = defaults.Edge;
        _settings.IconGap = defaults.IconGap;
        _settings.BottomMargin = defaults.BottomMargin;
        _settings.MagnifyMaxScale = defaults.MagnifyMaxScale;

        _settings.ShowBrandIcon = defaults.ShowBrandIcon;
        _settings.ShowWindowsIcon = defaults.ShowWindowsIcon;
        _settings.ShowLaunchpadIcon = defaults.ShowLaunchpadIcon;
        _settings.ShowComputerIcon = defaults.ShowComputerIcon;
        _settings.ShowTrashIcon = defaults.ShowTrashIcon;
        _settings.ShowWindowPreviews = defaults.ShowWindowPreviews;
        _settings.PreviewIconOnly = defaults.PreviewIconOnly;
        _settings.TopMode = defaults.TopMode;
        _settings.ShowBackgroundAppsButton = defaults.ShowBackgroundAppsButton;
        _settings.ShowBatteryPercent = defaults.ShowBatteryPercent;

        _settings.HideTaskbar = defaults.HideTaskbar;
        _settings.HideDesktopIcons = defaults.HideDesktopIcons;
        _settings.PauseMode = defaults.PauseMode;

        _settings.ShowIsland = defaults.ShowIsland;
        _settings.IslandFixed = defaults.IslandFixed;
        _settings.IslandWidth = defaults.IslandWidth;
        _settings.IslandHeight = defaults.IslandHeight;
        _settings.IslandPlayerWidth = defaults.IslandPlayerWidth;
        _settings.IslandPlayerHeight = defaults.IslandPlayerHeight;
        _settings.IslandX = defaults.IslandX;
        _settings.IslandY = defaults.IslandY;
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(SettingsStore.RootDirectory);

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.GetTempPath());

    /// <summary>打开安装根目录（%LocalAppData%\AquaDesk 或自定义 --installto 的位置）。</summary>
    private void OnOpenInstallFolder(object sender, RoutedEventArgs e)
    {
#if VELOPACK
        var root = Velopack.Locators.VelopackLocator.Current.RootAppDir;
#else
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaDesk");
#endif

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaDesk");
        }

        OpenFolder(root);
    }

    /// <summary>
    /// 卸载：删配置目录 + 调 Update.exe --uninstall（删安装目录 / 快捷方式 / 注册表卸载项）。
    /// Velopack 自带的卸载不碰 %AppData%（Roaming）里的配置，所以配置要在这里单独删。
    /// </summary>
    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "卸载将删除 AquaDesk 程序本体、快捷方式，以及所有配置数据\n" +
            "（settings.json、dock.json、launchpad.json 等）。\n\n" +
            "此操作不可恢复，确定卸载吗？",
            "卸载 AquaDesk",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        // 1) 先删配置目录（进程还活着，删得掉）。失败不阻塞卸载，残留可手动清理。
        try
        {
            var config = SettingsStore.RootDirectory;
            if (Directory.Exists(config)) Directory.Delete(config, recursive: true);
        }
        catch (Exception ex)
        {
            App.Log("卸载：删除配置目录失败", ex);
        }

        // 2) 调 Update.exe --uninstall：删安装目录 + 快捷方式 + 注册表卸载项，并结束本进程。
        try
        {
#if VELOPACK
            var update = Velopack.Locators.VelopackLocator.Current.UpdateExePath;
#else
            var update = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaDesk", "Update.exe");
#endif

            if (string.IsNullOrWhiteSpace(update) || !File.Exists(update))
            {
                // 兜底：找不到 Update.exe（异常安装），直接删安装目录再退出
#if VELOPACK
                var root = Velopack.Locators.VelopackLocator.Current.RootAppDir;
#else
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaDesk");
#endif
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                    Directory.Delete(root, recursive: true);

                Application.Current.Shutdown();
                return;
            }

            Process.Start(new ProcessStartInfo(update, "--uninstall")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.Log("卸载：调用卸载失败", ex);
            MessageBox.Show(
                "卸载失败，请从「设置 → 应用 → 已安装的应用」里卸载 AquaDesk。",
                "卸载", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.Log("设置页：打开目录失败", ex);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>灵动岛恢复默认：位置回到顶部居中，时间形态 88×30、播放形态 256×34。</summary>
    private void OnResetIsland(object sender, RoutedEventArgs e)
    {
        _settings.IslandX = -1;
        _settings.IslandY = -1;
        _settings.IslandWidth = 88;
        _settings.IslandHeight = 30;
        _settings.IslandPlayerWidth = 256;
        _settings.IslandPlayerHeight = 34;
    }

    // ---------------- 保存 ----------------

    /// <summary>
    /// 设置项一改就叫一声。改动本身是实时生效的（Dock 立刻变），
    /// 这里只负责把「保存」按钮亮出来 —— 写盘交给用户点，或者关窗时兜底。
    /// </summary>
    private void OnAnySettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_dirty) return;

        _dirty = true;
        FooterPart("SavedHint")?.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        FooterPart("SaveButton")?.SetValue(UIElement.VisibilityProperty, Visibility.Visible);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        SettingsStore.Save(_settings);

        _dirty = false;
        FooterPart("SaveButton")?.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

        // 「已保存」显示一小会儿就自己收起来，别留个常驻标签占位
        FooterPart("SavedHint")?.SetValue(UIElement.VisibilityProperty, Visibility.Visible);

        _savedHintTimer?.Stop();
        _savedHintTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.8)
        };

        _savedHintTimer.Tick += (_, _) =>
        {
            _savedHintTimer?.Stop();
            FooterPart("SavedHint")?.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        };

        _savedHintTimer.Start();
    }

    /// <summary>
    /// 底部那条按钮栏是 TabControl 模板的一部分（所有分页共用一条），
    /// 所以「保存」和「已保存」拿不到同名字段，只能按名字从模板里取。
    /// </summary>
    private FrameworkElement? FooterPart(string name) =>
        RootTabs.Template?.FindName(name, RootTabs) as FrameworkElement;

    /// <summary>关窗（×／Esc／完成）时兜底保存一次：改完直接点「完成」也不会丢。</summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.PropertyChanged -= OnAnySettingChanged;
        _savedHintTimer?.Stop();

        if (_dirty) SettingsStore.Save(_settings);
        _dirty = false;
    }
}
