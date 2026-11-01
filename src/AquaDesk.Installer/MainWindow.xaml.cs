using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace AquaDesk.Installer;

public partial class MainWindow : Window
{
    private string _installedLocation = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        var installed = FindInstalledLocation();

        if (installed.Length > 0 && Directory.Exists(installed))
        {
            _installedLocation = installed;

            // 已安装：直接给「打开安装目录 / 卸载 / 关闭」，不再跑 Setup.exe 弹那个「已安装」窗口
            TitleText.Text = "AquaDesk 已安装";
            SubtitleText.Text = "检测到这台电脑上已经装过 AquaDesk";
            InstallView.Visibility = Visibility.Collapsed;
            InstalledView.Visibility = Visibility.Visible;
            InstalledPathText.Text = installed;

            CancelButton.Content = "关闭";
            InstallButton.Visibility = Visibility.Collapsed;
            UninstallButton.Visibility = Visibility.Visible;
            OpenDirButton.Visibility = Visibility.Visible;
        }
        else
        {
            PathBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaDesk-net48");
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择安装位置",
            InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : string.Empty,
        };

        if (dialog.ShowDialog() == true)
        {
            PathBox.Text = dialog.FolderName;
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e) =>
        await RunSetup(PathBox.Text?.Trim() ?? string.Empty);

    /// <summary>跑 Setup.exe --installto。首次安装走这里。</summary>
    private async Task RunSetup(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            SetStatus("请先选择安装位置");
            return;
        }

        // 安装包必须和引导器放在同一目录
        var setup = Path.Combine(AppContext.BaseDirectory, "AquaDesk-win-Setup.exe");

        if (!File.Exists(setup))
        {
            SetStatus("未找到安装包 AquaDesk-win-Setup.exe，请确认它和本程序在同一目录");
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            SetStatus("无法创建安装目录：" + ex.Message);
            return;
        }

        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        SetStatus("正在安装…");

        try
        {
            var psi = new ProcessStartInfo(setup, $"--installto \"{dir}\"")
            {
                UseShellExecute = false,
            };

            // 安装要几秒到几十秒，放后台线程等，别把窗口冻成「未响应」
            await Task.Run(() =>
            {
                using var process = Process.Start(psi);
                process?.WaitForExit();
            });

            SetStatus("安装完成");
            InstallButton.Content = "完成";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= OnInstall;
            InstallButton.Click += (_, _) => Close();
        }
        catch (Exception ex)
        {
            SetStatus("安装失败：" + ex.Message);
            InstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
        }
    }

    private void OnOpenDir(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_installedLocation)) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_installedLocation}\"")
            {
                UseShellExecute = true
            });
            Close();
        }
        catch (Exception ex)
        {
            InstalledStatusText.Text = "打开安装目录失败：" + ex.Message;
        }
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        var update = Path.Combine(_installedLocation, "Update.exe");

        if (!File.Exists(update))
        {
            InstalledStatusText.Text = "没有找到卸载程序，请从「设置 → 应用 → 已安装的应用」里卸载";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(update, "--uninstall")
            {
                UseShellExecute = true
            });
            Close();
        }
        catch (Exception ex)
        {
            InstalledStatusText.Text = "启动卸载失败：" + ex.Message;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        InstalledStatusText.Text = message;
    }

    /// <summary>读 Velopack 注册的卸载表，拿安装位置。没装过返回空串。</summary>
    private static string FindInstalledLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AquaDesk");

            return key?.GetValue("InstallLocation") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
