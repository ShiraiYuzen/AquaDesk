using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AquaDesk.Services;

namespace AquaDesk.Views;

/// <summary>
/// 「检查更新」的结果卡片。
///
/// 有新版时可以下载并安装：下载 → 校验（大小 / SHA256 / 签名指纹）→
/// 交给 <see cref="UpdateService.ApplyAndRestart"/> 替换并重启。
/// 任何一步失败都不会动当前安装，只把原因写在进度条那一行，并留一个「打开发布页」。
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateCheckResult _result;

    private readonly CancellationTokenSource _cancel = new();

    private bool _busy;

    private string? _diagnostics;

    public UpdateDialog(UpdateCheckResult result)
    {
        _result = result;
        InitializeComponent();

        Apply(result);

        Loaded += (_, _) =>
        {
            // CenterOwner 在 SizeToContent 下会用「排版前的高度」算位置，结果偏上小半屏。
            // 等真正排完版再按实际尺寸居中一次。没有 Owner（--update-demo）就居中到屏幕。
            if (Owner is not null)
            {
                Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
                Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
            }
            else
            {
                var area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - ActualWidth) / 2;
                Top = area.Top + (area.Height - ActualHeight) / 2;
            }
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) Close();
        };

        Closing += (_, _) => _cancel.Cancel();
    }

    private void Apply(UpdateCheckResult result)
    {
        switch (result.Status)
        {
            case UpdateStatus.Available when result.Info is not null:
            {
                TitleText.Text = "发现新版本";
                SubtitleText.Text = $"当前 {UpdateService.CurrentVersionText} → 最新 {result.Info.Version}";

                RenderNotes(NotesText, result.Info.Notes);

                NotesBox.Visibility = Visibility.Visible;
                UpdateButton.Visibility = Visibility.Visible;
                SkipCheck.Visibility = Visibility.Visible;
                CancelButton.Content = "稍后";

                // 已经跳过过的版本，把勾选状态亮出来，省得用户以为没记住
                SkipCheck.IsChecked = UpdateService.IsSkipped(result.Info.Version);

                Title = $"发现新版本 {result.Info.Version}";
                break;
            }

            case UpdateStatus.UpToDate:
            {
                TitleText.Text = "已是最新版本";
                SubtitleText.Text = $"当前版本 {UpdateService.CurrentVersionText}";

                GlyphText.Text = "✓";
                CancelButton.Content = "好的";
                Title = "已是最新版本";
                break;
            }

            default:
            {
                TitleText.Text = "检查更新失败";
                SubtitleText.Text = string.IsNullOrWhiteSpace(result.Message)
                    ? "请稍后重试"
                    : result.Message;

                GlyphText.Text = "!";
                CancelButton.Content = "关闭";
                DiagButton.Visibility = Visibility.Visible;
                _diagnostics = result.Diagnostics;
                Title = "检查更新失败";
                break;
            }
        }
    }

    /// <summary>
    /// 把 Release 的 Markdown body 渲染成 TextBlock 的 Inlines，不引第三方库。
    /// 覆盖 GitHub 发版说明里最常见的几种标记：标题、列表、**粗体**、`行内代码`、
    /// [链接](url)、```代码块```；图片 / 徽章这类在提示框里没意义，直接跳过。
    /// </summary>
    private static void RenderNotes(TextBlock block, string notes)
    {
        block.Inlines.Clear();

        if (string.IsNullOrWhiteSpace(notes))
        {
            block.Inlines.Add(new Run("这个版本没有写更新说明。"));
            return;
        }

        var lines = notes.Replace("\r\n", "\n").Split('\n');
        var inFence = false;
        var fenceLines = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            // 代码围栏：``` 开始 / 结束
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceLines.Clear();
                }
                else
                {
                    inFence = false;
                    block.Inlines.Add(new LineBreak());
                    block.Inlines.Add(new Run(string.Join("\n", fenceLines))
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11.5,
                    });
                    block.Inlines.Add(new LineBreak());
                }
                continue;
            }

            if (inFence)
            {
                fenceLines.Add(line);
                continue;
            }

            // 图片 / 徽章
            if (trimmed.StartsWith("![", StringComparison.Ordinal)) continue;

            // 标题：### 修复 → 半粗、稍大
            if (trimmed.StartsWith('#'))
            {
                var text = trimmed.TrimStart('#').TrimStart();
                if (text.Length == 0) continue;

                block.Inlines.Add(new LineBreak());
                block.Inlines.Add(new Run(text) { FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
                block.Inlines.Add(new LineBreak());
                continue;
            }

            // 分隔线
            if (trimmed.Length >= 3 && trimmed.All(c => c is '-' or '*'))
            {
                block.Inlines.Add(new LineBreak());
                continue;
            }

            // 列表项保留原样，只把 "- " 换成 "· "
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                AppendInline(block, "·  " + trimmed[2..]);
            }
            else
            {
                AppendInline(block, line);
            }

            block.Inlines.Add(new LineBreak());
        }
    }

    /// <summary>解析一行里的内联标记：**粗体**、`行内代码`、[链接](url)。</summary>
    private static void AppendInline(TextBlock block, string text)
    {
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            // **粗体**
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    block.Inlines.Add(new Run(text[(i + 2)..end]) { FontWeight = FontWeights.Bold });
                    i = end + 2;
                    continue;
                }
            }

            // `行内代码`
            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    block.Inlines.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11.5,
                    });
                    i = end + 1;
                    continue;
                }
            }

            // [文字](链接)
            if (c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > i && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var paren = text.IndexOf(')', close + 2);
                    if (paren > close)
                    {
                        var label = text[(i + 1)..close];
                        var url = text[(close + 2)..paren];

                        var link = new Hyperlink(new Run(label))
                        {
                            Foreground = new SolidColorBrush(Color.FromRgb(0x5F, 0xD2, 0xFF)),
                            TextDecorations = null,
                        };
                        link.Click += (_, _) => OpenLink(url);

                        block.Inlines.Add(link);
                        i = paren + 1;
                        continue;
                    }
                }
            }

            // 普通字符：一直累积到下一个特殊标记
            var next = i;
            while (next < text.Length)
            {
                var ch = text[next];
                if ((ch == '*' && next + 1 < text.Length && text[next + 1] == '*')
                    || ch == '`'
                    || ch == '[')
                {
                    break;
                }
                next++;
            }

            if (next > i) block.Inlines.Add(new Run(text[i..next]));
            i = next;
        }
    }

    private static void OpenLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log("更新：打开链接失败", ex);
        }
    }

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_diagnostics))
        {
            _diagnostics = $"AquaDesk {UpdateService.CurrentVersionText}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                           $"系统：{Environment.OSVersion}\n运行时：.NET {Environment.Version}";
        }

        try
        {
            Clipboard.SetText(_diagnostics);
            DiagButton.Content = "已复制";
        }
        catch (Exception ex)
        {
            App.Log("更新：复制诊断信息失败", ex);
        }
    }

    /// <summary>下载 → 校验 → 安排替换重启。中途失败只改这一行的提示，不动窗口结构。</summary>
    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy || _result.Info is null) return;

        _busy = true;

        UpdateButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        SkipCheck.IsEnabled = false;

        ProgressPanel.Visibility = Visibility.Visible;
        ProgressLabel.Text = "正在下载…";
        ProgressPercent.Text = "0%";
        DownloadBar.Value = 0;

        var progress = new Progress<double>(value =>
        {
            DownloadBar.Value = value;
            ProgressPercent.Text = $"{value * 100:F0}%";
            if (value >= 1) ProgressLabel.Text = "正在校验签名…";
        });

        var (package, error) = await UpdateService.DownloadAsync(_result.Info, progress, _cancel.Token);

        if (package is null)
        {
            ShowFailure(error);
            return;
        }

        ProgressLabel.Text = "校验通过，正在重启…";
        ProgressPercent.Text = "100%";

        if (!UpdateService.ApplyAndRestart(package))
        {
            ShowFailure("替换当前程序失败，可以打开发布页手动下载");
            return;
        }

        // 给 PowerShell 一点时间把自己挂到「等待本进程退出」上
        CancelButton.IsEnabled = false;
        await Task.Delay(500);

        Application.Current.Shutdown();
    }

    private void ShowFailure(string message)
    {
        _busy = false;

        ProgressLabel.Text = message;
        ProgressPercent.Text = string.Empty;
        DownloadBar.Value = 0;

        UpdateButton.Content = "重试";
        UpdateButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        PageButton.Visibility = Visibility.Visible;
        SkipCheck.IsEnabled = true;
    }

    private void OnOpenPage(object sender, RoutedEventArgs e) => OpenPage();

    private void OpenPage()
    {
        var url = _result.Info?.PageUrl;

        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            App.Log($"更新：打开发布页 {url}", null);
        }
        catch (Exception ex)
        {
            App.Log("更新：打开发布页失败", ex);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_result.Status == UpdateStatus.Available && _result.Info is not null)
        {
            if (SkipCheck.IsChecked == true)
            {
                UpdateService.SkipVersion(_result.Info.Version);
                UpdateService.SaveState();
                App.Log($"更新：已跳过 {_result.Info.Version}", null);
            }
            else if (UpdateService.IsSkipped(_result.Info.Version))
            {
                // 之前跳过过，这次取消勾选就取消跳过
                UpdateService.SkipVersion(string.Empty);
                UpdateService.SaveState();
            }
        }

        Close();
    }

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
