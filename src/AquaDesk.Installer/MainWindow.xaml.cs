using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace AquaDesk.Installer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaDesk");
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

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        var dir = PathBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(dir))
        {
            StatusText.Text = "请先选择安装位置";
            return;
        }

        // 安装包必须和引导器放在同一目录
        var setup = Path.Combine(AppContext.BaseDirectory, "AquaDesk-win-Setup.exe");

        if (!File.Exists(setup))
        {
            StatusText.Text = "未找到安装包 AquaDesk-win-Setup.exe，请确认它和本程序在同一目录";
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法创建安装目录：" + ex.Message;
            return;
        }

        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        BrowseButton.IsEnabled = false;
        StatusText.Text = "正在安装…";

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

            StatusText.Text = "安装完成";
            InstallButton.Content = "完成";
            InstallButton.IsEnabled = true;
            InstallButton.Click -= OnInstall;
            InstallButton.Click += (_, _) => Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "安装失败：" + ex.Message;
            InstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            BrowseButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
