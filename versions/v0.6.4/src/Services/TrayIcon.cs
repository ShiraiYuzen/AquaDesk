using System.Drawing;
using WinForms = System.Windows.Forms;

namespace AquaDesk.Services;

/// <summary>
/// 托盘图标（通知区域那个小图标）。
///
/// 用 WinForms 的 NotifyIcon，而不是手写 Shell_NotifyIcon + 隐藏窗口：
/// WPF 应用里 NotifyIcon 直接能用，省掉一整套消息循环和结构体编组。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ToolStripMenuItem _toggleItem;

    public TrayIcon(Action toggleDock, Action openSettings, Action exit, Func<bool> isDockVisible)
    {
        _toggleItem = new WinForms.ToolStripMenuItem("隐藏 Dock", null, (_, _) => toggleDock());
        var settingsItem = new WinForms.ToolStripMenuItem("Dock 设置…", null, (_, _) => openSettings());
        var exitItem = new WinForms.ToolStripMenuItem("退出 AquaDesk", null, (_, _) => exit());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        // 每次弹出前刷新文案，避免显示与实际状态不一致
        menu.Opening += (_, _) =>
            _toggleItem.Text = isDockVisible() ? "隐藏 Dock" : "显示 Dock";

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "AquaDesk 水光桌面",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => toggleDock();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("Assets/AquaDesk.ico", UriKind.Relative));

            if (resource?.Stream is { } stream)
            {
                using (stream)
                {
                    return new Icon(stream);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 托盘图标加载失败：{ex.Message}");
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
