using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AquaDesk.Services;

namespace AquaDesk.Views;

/// <summary>
/// 菜单栏的下拉面板：Wi-Fi 列表 / Wi-Fi 密码 / 音量，三选一显示。
/// </summary>
public partial class MenuBarPanel : UserControl
{
    public event Action? WifiStateChanged;
    public event Action<int>? VolumeChanged;

    /// <summary>用户在下拉框里换了键盘布局，菜单栏的指示要跟着刷新。</summary>
    public event Action? InputMethodChanged;

    /// <summary>面板里点了「要跳走」的项（比如打开系统设置），请菜单栏把面板收起来。</summary>
    public event Action? RequestClose;

    private string? _pendingSsid;
    private string? _wifiNotice;
    private bool _draggingVolume;

    /// <summary>上一次画出来的音量值，用来判断系统音量是不是真的变了。</summary>
    private int _lastVolume = int.MinValue;

    /// <summary>
    /// 音量面板开着时盯着系统音量：用户按键盘音量键、插拔耳机、别的程序调音量，
    /// 条子都要跟着动。150ms 一次，比人手快，也不会把 CoreAudio 打爆。
    /// </summary>
    private readonly DispatcherTimer _volumeWatch = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(150)
    };

    public MenuBarPanel()
    {
        InitializeComponent();
        _volumeWatch.Tick += OnVolumeWatchTick;
    }

    // ---------------- Wi-Fi ----------------

    public void ShowWifi()
    {
        ShowOnly(WifiView);

        WifiSwitch.IsChecked = WifiService.IsRadioOn();

        WifiStatusText.Text = "正在扫描…";
        WifiList.Children.Clear();
        WifiEmptyText.Text = "正在扫描…";
        WifiEmptyText.Visibility = Visibility.Visible;

        Task.Run(() => WifiService.Query(true)).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                Dispatcher.Invoke(() =>
                {
                    WifiEmptyText.Text = "扫描失败";
                    WifiEmptyText.Visibility = Visibility.Visible;
                });
                return;
            }

            Dispatcher.Invoke(() => BuildWifiList(task.Result));
        });
    }

    private void BuildWifiList(WifiSnapshot snapshot)
    {
        WifiList.Children.Clear();

        var networks = snapshot.Networks;

        // 连接失败之类的提示要在扫描结束后再显示，否则会被 ShowWifi 里的「正在扫描…」盖掉
        var notice = _wifiNotice;
        _wifiNotice = null;

        // 状态文案：已连接哪张网 / 未连接 / 开关关着
        WifiStatusText.Text = snapshot.ConnectedSsid is not null
            ? $"已连接 {snapshot.ConnectedSsid}"
            : WifiService.IsRadioOn() ? "未连接" : "Wi-Fi 已关闭";

        if (networks.Count == 0)
        {
            WifiEmptyText.Text = notice ?? "没有扫描到网络";
            WifiEmptyText.Visibility = Visibility.Visible;
            return;
        }

        if (notice is null)
        {
            WifiEmptyText.Visibility = Visibility.Collapsed;
        }
        else
        {
            WifiEmptyText.Text = notice;
            WifiEmptyText.Visibility = Visibility.Visible;
        }

        foreach (var network in networks)
        {
            WifiList.Children.Add(CreateWifiRow(network));

            // 已连接的那条下面挂一个「断开连接」，和 Windows 一致
            if (network.Connected)
            {
                WifiList.Children.Add(CreateDisconnectRow());
            }
        }
    }

    private UIElement CreateDisconnectRow()
    {
        // macOS 里这种操作是文字链接，不是带框的按钮
        var button = new Button
        {
            Content = "断开连接",
            Style = (Style)FindResource("LinkButton"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, -2, 6, 8)
        };

        button.Click += (_, _) =>
        {
            WifiService.Disconnect();
            ShowWifi();
        };

        return button;
    }

    private UIElement CreateWifiRow(WifiNetwork network)
    {
        // macOS 的做法：每个网络是一张独立的小卡片，不是「一行一行带分隔线」的列表
        var baseColor = network.Connected
            ? Color.FromArgb(0x26, 0x0A, 0x84, 0xFF)
            : Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);

        var hoverColor = network.Connected
            ? Color.FromArgb(0x36, 0x0A, 0x84, 0xFF)
            : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF);

        var card = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 5),
            Background = new SolidColorBrush(baseColor),
            Cursor = Cursors.Hand,
            Tag = network
        };

        card.MouseEnter += (_, _) => card.Background = new SolidColorBrush(hoverColor);
        card.MouseLeave += (_, _) => card.Background = new SolidColorBrush(baseColor);
        card.MouseLeftButtonUp += OnWifiCardClick;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左边：信号格 +（加密时）小锁
        var icons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        icons.Children.Add(CreateSignalIcon(network.Signal));

        if (network.Secured)
        {
            var padlock = CreateLockIcon();
            padlock.Margin = new Thickness(5, 0, 0, 0);
            icons.Children.Add(padlock);
        }

        Grid.SetColumn(icons, 0);
        grid.Children.Add(icons);

        // 中间：名字（已连接的下面再挂一行状态）
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        text.Children.Add(new TextBlock
        {
            Text = network.Ssid,
            Foreground = new SolidColorBrush(network.Connected
                ? Color.FromRgb(0x0A, 0x84, 0xFF)
                : Color.FromRgb(0xF2, 0xF2, 0xF2)),
            FontSize = 13,
            FontWeight = network.Connected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (network.Connected)
        {
            text.Children.Add(new TextBlock
            {
                Text = "已连接",
                Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (network.Connected)
        {
            var check = new TextBlock
            {
                Text = "✓",
                Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        card.Child = grid;
        return card;
    }

    private void OnWifiCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WifiNetwork network }) return;
        if (network.Connected) return;

        // 开放网络、或以前连过的，直接连；否则要密码
        if (!network.Secured || network.HasProfile)
        {
            TryConnect(network.Ssid, null);
            return;
        }

        ShowPassword(network);
    }

    /// <summary>加密网络的小锁，和 Windows 列表里那个一样贴在名字左边。</summary>
    private static FrameworkElement CreateLockIcon()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

        var canvas = new Canvas
        {
            Width = 9,
            Height = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var shackle = new Path
        {
            Data = Geometry.Parse("M1.6,5.4 L1.6,3.6 A2.9,2.9 0 0,1 7.4,3.6 L7.4,5.4"),
            Stroke = brush,
            StrokeThickness = 1.2,
            Fill = Brushes.Transparent
        };

        canvas.Children.Add(shackle);

        var body = new Rectangle
        {
            Width = 9,
            Height = 6,
            RadiusX = 1.4,
            RadiusY = 1.4,
            Fill = brush
        };

        Canvas.SetTop(body, 5);
        canvas.Children.Add(body);

        return canvas;
    }

    /// <summary>
    /// 信号格图标：三弧 + 圆点，和菜单栏那个是同一套几何。
    /// 65% 以上三弧全亮，30% 以上亮两弧，再低只亮底部圆点。
    /// </summary>
    private static FrameworkElement CreateSignalIcon(int signal)
    {
        var lit = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        var dim = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        var grid = new Grid
        {
            Width = 17,
            Height = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = $"{signal}%"
        };

        grid.Children.Add(Arc("M1.5,5.2 A11,11 0 0,1 15.5,5.2", signal >= 65 ? lit : dim));
        grid.Children.Add(Arc("M4.4,8.2 A7,7 0 0,1 12.6,8.2", signal >= 30 ? lit : dim));

        grid.Children.Add(new Ellipse
        {
            Width = 2.6,
            Height = 2.6,
            Fill = lit,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1)
        });

        return grid;
    }

    private static Path Arc(string data, Brush stroke) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = stroke,
        StrokeThickness = 1.6,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Fill = Brushes.Transparent
    };

    // ---------------- Wi-Fi 开关 / 底部按钮 ----------------

    private void OnWifiSwitchClick(object sender, RoutedEventArgs e)
    {
        // Click 触发时 IsChecked 已经是新值
        var wantOn = WifiSwitch.IsChecked == true;

        if (WifiService.SetRadioOn(wantOn))
        {
            ShowWifi();
            return;
        }

        // 改不动就回滚，别让开关显示成一个假状态
        WifiSwitch.IsChecked = !wantOn;
        _wifiNotice = "切换失败，请重试";
        ShowWifi();
    }

    private void OnWifiRefreshClick(object sender, RoutedEventArgs e) => ShowWifi();

    private void OnMoreWifiClick(object sender, RoutedEventArgs e)
    {
        // 先收面板再跳转，否则面板会一直挂在那儿等「点外面」的轮询
        RequestClose?.Invoke();

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-wifi")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 打开 Wi-Fi 设置失败：{ex.Message}");
        }
    }

    private void ShowPassword(WifiNetwork network)
    {
        _pendingSsid = network.Ssid;

        WifiPasswordTitle.Text = $"连接到「{network.Ssid}」";
        WifiPasswordBox.Password = string.Empty;
        WifiPasswordPlaceholder.Visibility = Visibility.Visible;
        WifiErrorText.Visibility = Visibility.Collapsed;

        ShowOnly(WifiPasswordView);
        WifiPasswordBox.Focus();
    }

    private void OnWifiPasswordChanged(object sender, RoutedEventArgs e) =>
        WifiPasswordPlaceholder.Visibility = WifiPasswordBox.Password.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnWifiBackClick(object sender, RoutedEventArgs e)
    {
        _pendingSsid = null;
        ShowWifi();
    }

    private void OnWifiConnectClick(object sender, RoutedEventArgs e)
    {
        if (_pendingSsid is null) return;

        var password = WifiPasswordBox.Password;

        if (password.Length < 8)
        {
            WifiErrorText.Text = "Wi-Fi 密码至少 8 位";
            WifiErrorText.Visibility = Visibility.Visible;
            return;
        }

        TryConnect(_pendingSsid, password);
    }

    private void TryConnect(string ssid, string? password)
    {
        WifiErrorText.Visibility = Visibility.Collapsed;
        WifiEmptyText.Text = "正在连接…";
        WifiEmptyText.Visibility = Visibility.Visible;

        Task.Run(() => WifiService.Connect(ssid, password)).ContinueWith(task =>
        {
            // 任务抛异常时直接取 .Result 会炸在 UI 线程上，先判状态
            var ok = task.IsCompletedSuccessfully && task.Result;

            Dispatcher.Invoke(() =>
            {
                if (ok)
                {
                    _pendingSsid = null;
                    WifiStateChanged?.Invoke();
                    ShowWifi();
                    return;
                }

                if (password is null)
                {
                    // 开放网络 / 已存配置：本来就不在密码页，失败就回列表并把原因留在列表下方
                    _wifiNotice = "连接失败";
                    ShowWifi();
                    return;
                }

                WifiErrorText.Text = "连接失败，请检查密码";
                WifiErrorText.Visibility = Visibility.Visible;
                ShowOnly(WifiPasswordView);
            });
        });
    }

    // ---------------- 键盘布局 ----------------

    /// <summary>
    /// 列出已安装的键盘布局，点一行切过去（等价于 Windows 那个「键盘布局」弹出层）。
    /// 比「点一下在中英之间来回切」通用：装了三种以上布局时也能选。
    /// </summary>
    public void ShowKeyboard()
    {
        ShowOnly(KeyboardView);

        KeyboardList.Children.Clear();

        foreach (var layout in KeyboardLayouts.GetAll())
        {
            KeyboardList.Children.Add(CreateKeyboardRow(layout));
        }
    }

    private UIElement CreateKeyboardRow(KeyboardLayout layout)
    {
        var button = new Button
        {
            Style = (Style)FindResource("RowButton"),
            Tag = layout
        };

        button.Click += OnKeyboardRowClick;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左边：有图标用图标，没有就用「ENG / 中」这种短代号
        FrameworkElement leading;

        if (layout.Icon is not null)
        {
            var image = new Image
            {
                Source = layout.Icon,
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform
            };

            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            leading = image;
        }
        else
        {
            leading = new TextBlock
            {
                Text = layout.Badge,
                Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                FontSize = 12,
                MinWidth = 26,
                TextAlignment = TextAlignment.Center
            };
        }

        leading.VerticalAlignment = VerticalAlignment.Center;
        leading.Margin = new Thickness(0, 0, 10, 0);

        Grid.SetColumn(leading, 0);
        grid.Children.Add(leading);

        // 中间：语言名 + 输入法名，和系统那个弹层一样两行
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        text.Children.Add(new TextBlock
        {
            Text = layout.Title,
            Foreground = new SolidColorBrush(layout.IsCurrent
                ? Color.FromRgb(0x0A, 0x84, 0xFF)
                : Color.FromRgb(0xF2, 0xF2, 0xF2)),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        text.Children.Add(new TextBlock
        {
            Text = layout.Subtitle,
            Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (layout.IsCurrent)
        {
            var check = new TextBlock
            {
                Text = "✓",
                Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        button.Content = grid;
        return button;
    }

    private void OnKeyboardRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KeyboardLayout layout }) return;
        if (layout.IsCurrent) return;

        if (KeyboardLayouts.Activate(layout)) InputMethodChanged?.Invoke();
    }

    private void OnMoreKeyboardClick(object sender, RoutedEventArgs e)
    {
        // 先把下拉框收起来，再跳系统设置——否则面板会一直挂在那儿，
        // 而且要等设置窗口抢到焦点后靠「点外面」的定时器才关，会闪一下
        RequestClose?.Invoke();

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:regionlanguage")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 打开键盘设置失败：{ex.Message}");
        }
    }

    // ---------------- 蓝牙 ----------------

    private readonly List<string> _newDeviceIds = new();

    /// <summary>蓝牙面板：开关 + 已保存的设备（带连接状态）+ 新设备 + 更多设置。</summary>
    public void ShowBluetooth()
    {
        ShowOnly(BluetoothView);

        PairedList.Children.Clear();
        NewDeviceList.Children.Clear();
        _newDeviceIds.Clear();
        BluetoothEmptyText.Visibility = Visibility.Collapsed;
        BluetoothStatusText.Text = "正在读取…";

        ReloadBluetooth();
    }

    private void ReloadBluetooth()
    {
        BluetoothService.DeviceFound -= OnBluetoothDeviceFound;
        BluetoothService.DeviceFound += OnBluetoothDeviceFound;

        BluetoothService.StopDiscovery();

        Task.Run(() =>
        {
            var on = BluetoothService.IsRadioOn();

            var paired = on ? BluetoothService.GetPaired() : new List<BluetoothDeviceInfo>();
            var cached = on ? BluetoothService.GetUnpairedCached() : new List<BluetoothDeviceInfo>();

            return (on, paired, cached);
        }).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;

            var (on, paired, cached) = task.Result;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                BluetoothSwitch.IsChecked = on;
                BluetoothStatusText.Text = on ? $"{paired.Count} 个已保存的设备" : "蓝牙已关闭";

                PairedList.Children.Clear();

                foreach (var device in paired)
                {
                    PairedList.Children.Add(CreateBluetoothRow(device));
                }

                NewDeviceList.Children.Clear();

                if (!on)
                {
                    return;
                }

                // 缓存里以前发现过的设备先列出来，不用干等广播
                foreach (var device in cached)
                {
                    if (_newDeviceIds.Contains(device.Id)) continue;

                    _newDeviceIds.Add(device.Id);
                    NewDeviceList.Children.Add(CreateBluetoothRow(device));
                }

                if (paired.Count == 0 && cached.Count == 0)
                {
                    BluetoothEmptyText.Text = "附近没有找到蓝牙设备";
                    BluetoothEmptyText.Visibility = Visibility.Visible;
                }

                BluetoothService.StartDiscovery();
            }));
        });
    }

    /// <summary>发现回调在后台线程，切回 UI 再建控件。</summary>
    private void OnBluetoothDeviceFound(BluetoothDeviceInfo device)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (BluetoothView.Visibility != Visibility.Visible) return;
            if (_newDeviceIds.Contains(device.Id)) return;

            _newDeviceIds.Add(device.Id);
            NewDeviceList.Children.Add(CreateBluetoothRow(device));
        }));
    }

    private UIElement CreateBluetoothRow(BluetoothDeviceInfo device)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 5),
            Background = new SolidColorBrush(device.Connected
                ? Color.FromArgb(0x26, 0x0A, 0x84, 0xFF)
                : Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF))
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = CreateBluetoothGlyph(device.Connected);
        glyph.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(glyph, 0);
        grid.Children.Add(glyph);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        text.Children.Add(new TextBlock
        {
            Text = device.Name,
            Foreground = new SolidColorBrush(device.Connected
                ? Color.FromRgb(0x0A, 0x84, 0xFF)
                : Color.FromRgb(0xF2, 0xF2, 0xF2)),
            FontSize = 13,
            FontWeight = device.Connected ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        text.Children.Add(new TextBlock
        {
            Text = device.Paired ? (device.Connected ? "已连接" : "未连接") : "未配对",
            Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0)
        });

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (!device.Paired)
        {
            var pair = new Button
            {
                Content = "配对",
                Style = (Style)FindResource("LinkButton"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = device
            };

            pair.Click += OnPairClick;

            Grid.SetColumn(pair, 2);
            grid.Children.Add(pair);
        }

        card.Child = grid;
        return card;
    }

    private void OnPairClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BluetoothDeviceInfo device }) return;

        Task.Run(() => BluetoothService.Pair(device.Id)).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully || !task.Result) return;

            // 配对成功后重新读一遍，设备会从「新设备」挪到「已保存」
            Dispatcher.BeginInvoke(new Action(ReloadBluetooth));
        });
    }

    private static FrameworkElement CreateBluetoothGlyph(bool connected)
    {
        var brush = new SolidColorBrush(connected
            ? Color.FromRgb(0x0A, 0x84, 0xFF)
            : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

        return new Path
        {
            Data = Geometry.Parse("M6.5,1 L6.5,13 M6.5,1 L10,3.8 L3,10.2 M6.5,13 L10,10.2 L3,3.8"),
            Stroke = brush,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 13,
            Height = 14,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void OnBluetoothSwitchClick(object sender, RoutedEventArgs e)
    {
        var wantOn = BluetoothSwitch.IsChecked == true;

        if (BluetoothService.SetRadioOn(wantOn))
        {
            ShowBluetooth();
            return;
        }

        BluetoothSwitch.IsChecked = !wantOn;
    }

    private void OnBluetoothRefreshClick(object sender, RoutedEventArgs e) => ReloadBluetooth();

    private void OnMoreBluetoothClick(object sender, RoutedEventArgs e)
    {
        RequestClose?.Invoke();

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 打开蓝牙设置失败：{ex.Message}");
        }
    }

    /// <summary>面板关掉时停止搜索，别让 DeviceWatcher 一直在后台跑。</summary>
    public void StopBluetooth() => BluetoothService.StopDiscovery();

    // ---------------- 音量 ----------------

    public void ShowVolume()
    {
        ShowOnly(VolumeView);

        // 读不到就传 -1，让 UI 显示 "--" 而不是假装音量是 0
        var volume = SystemStatus.GetVolume();
        if (volume < 0) volume = AudioService.GetVolume();
        UpdateVolumeBar(volume);

        OutputList.Children.Clear();
        InputList.Children.Clear();

        // CoreAudio 的 COM 在 MTA 线程（Task.Run）上会失败，这里必须留在 UI 线程调
        var outputs = AudioService.GetDevices(false);
        var inputs = AudioService.GetDevices(true);

        foreach (var device in outputs) OutputList.Children.Add(CreateDeviceRow(device));
        foreach (var device in inputs) InputList.Children.Add(CreateDeviceRow(device));

        if (outputs.Count == 0)
        {
            OutputList.Children.Add(CreateHintRow("未检测到输出设备"));
        }

        if (inputs.Count == 0)
        {
            InputList.Children.Add(CreateHintRow("未检测到输入设备"));
        }

        // 面板开着就一直跟：键盘音量键、别的程序调音量都要反映到条子上
        _volumeWatch.Start();
    }

    /// <summary>
    /// 跟随系统音量。三条规则：
    /// 拖动中不跟（否则手指和系统来回抢，条子会抖）；
    /// 读不到（-1）不跟（别把条子拉回 0）；
    /// 值没变不重画（150ms 一次，白白重排没意义）。
    ///
    /// 定时器的启停完全由 <see cref="ShowVolume"/> / <see cref="StopLiveUpdates"/> 控制，
    /// 不靠 IsVisible 判断 —— Popup 里的元素是否算「可见」有坑，一旦判错定时器会在第一拍就停掉。
    /// </summary>
    private void OnVolumeWatchTick(object? sender, EventArgs e)
    {
        if (_draggingVolume) return;

        var volume = AudioService.GetVolume();
        if (volume < 0 || volume == _lastVolume) return;

        UpdateVolumeBar(volume);
    }

    /// <summary>面板关掉时由菜单栏调用，停掉跟随定时器。</summary>
    public void StopLiveUpdates() => _volumeWatch.Stop();

    private UIElement CreateDeviceRow(AudioDevice device)
    {
        var button = new Button
        {
            Style = (Style)FindResource("RowButton"),
            Tag = device
        };

        button.Click += OnDeviceClick;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 圆形指示：当前默认设备实心，其它空心
        var dot = new Ellipse
        {
            Width = 12,
            Height = 12,
            StrokeThickness = 1.5,
            Stroke = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            Fill = device.IsDefault
                ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
                : Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var name = new TextBlock
        {
            Text = device.Name,
            Foreground = new SolidColorBrush(device.IsDefault
                ? Color.FromRgb(0x0A, 0x84, 0xFF)
                : Color.FromRgb(0xF2, 0xF2, 0xF2)),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        button.Content = grid;
        return button;
    }

    private UIElement CreateHintRow(string text) => new TextBlock
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
        FontSize = 12,
        Margin = new Thickness(31, 6, 0, 6)
    };

    private void OnDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AudioDevice device }) return;
        if (device.IsDefault) return;

        if (AudioService.SetDefaultDevice(device.Id)) ShowVolume();
    }

    private void UpdateVolumeBar(int percent)
    {
        var width = VolumeBar.ActualWidth > 0 ? VolumeBar.ActualWidth : 280;
        var known = percent >= 0;
        var value = known ? Math.Clamp(percent, 0, 100) : 50;
        var filled = width * value / 100.0;

        VolumeFill.Width = filled;
        VolumeKnob.Margin = new Thickness(filled - 6, 0, 0, 0);
        VolumePercentText.Text = known ? $"{value}%" : "--";

        // 记下来，跟随定时器才知道「变了没有」
        _lastVolume = known ? value : int.MinValue;
    }

    private void OnVolumeBarDown(object sender, MouseButtonEventArgs e)
    {
        _draggingVolume = true;
        VolumeBar.CaptureMouse();
        ApplyVolumeAt(e.GetPosition(VolumeBar).X);
    }

    private void OnVolumeBarMove(object sender, MouseEventArgs e)
    {
        if (!_draggingVolume) return;
        ApplyVolumeAt(e.GetPosition(VolumeBar).X);
    }

    private void OnVolumeBarUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingVolume) return;

        _draggingVolume = false;
        VolumeBar.ReleaseMouseCapture();
    }

    private void ApplyVolumeAt(double x)
    {
        var width = VolumeBar.ActualWidth;
        if (width <= 0) return;

        var percent = (int)Math.Round(Math.Clamp(x / width, 0, 1) * 100);

        UpdateVolumeBar(percent);

        // 真的写进系统，否则条子只是画了个样子
        AudioService.SetVolume(percent);

        // 通知菜单栏把图标波形刷新一下
        VolumeChanged?.Invoke(percent);
    }

    // ---------------- 后台程序 ----------------

    /// <summary>已经点过一次「结束」、等着二次确认的进程。重画列表时清空。</summary>
    private readonly HashSet<uint> _confirmEnd = new();

    /// <summary>exe 路径 → 图标，避免每次刷新都去问一遍 Shell。</summary>
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public void ShowBackground()
    {
        ShowOnly(BackgroundView);
        ReloadBackground();
    }

    private void OnBackgroundRefresh(object sender, RoutedEventArgs e) => ReloadBackground();

    private void ReloadBackground()
    {
        BackgroundList.Children.Clear();
        _confirmEnd.Clear();

        // 枚举窗口 + 读注册表 + 抠图标都在毫秒级，留在 UI 线程就行；
        // 真碰上进程特别多的机器再挪到线程池，别为保险先加一层异步复杂度
        var apps = BackgroundApps.Query();

        BackgroundCount.Text = apps.Count == 0 ? string.Empty : $"{apps.Count} 个";

        BackgroundHint.Text = apps.Count == 0
            ? "没有正在后台运行的程序"
            : "最小化的窗口点一下切回去；托盘程序只能结束";

        foreach (var app in apps)
        {
            BackgroundList.Children.Add(CreateBackgroundRow(app));
        }
    }

    private UIElement CreateBackgroundRow(BackgroundApp app)
    {
        var row = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 0, 0, 3),
            Background = new SolidColorBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)),
            Cursor = app.TrayOnly ? Cursors.Arrow : Cursors.Hand,
            Tag = app,
            ToolTip = app.Path.Length == 0 ? null : app.Path
        };

        if (!app.TrayOnly)
        {
            var idle = row.Background;
            var hover = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

            row.MouseEnter += (_, _) => row.Background = hover;
            row.MouseLeave += (_, _) => row.Background = idle;
            row.MouseLeftButtonUp += OnBackgroundRowClick;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = CreateAppIcon(app);
        icon.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        text.Children.Add(new TextBlock
        {
            Text = app.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        text.Children.Add(new TextBlock
        {
            Text = app.TrayOnly ? app.Detail : (app.Minimized ? $"最小化 · {app.Detail}" : app.Detail),
            Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
            FontSize = 10.5,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // 右边：窗口程序给「切到前台」提示 + 「结束」，托盘程序只有「结束」。
        // 结束按钮对所有行都要有 —— 后台卡死的程序只能靠它收掉。
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!app.TrayOnly) actions.Children.Add(CreateSwitchHint());
        actions.Children.Add(CreateEndButton(app));

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        row.Child = grid;
        return row;
    }

    /// <summary>窗口程序右边放一个弱提示，点整行就能切过去；结束按钮另外给。</summary>
    private static FrameworkElement CreateSwitchHint() => new TextBlock
    {
        Text = "切到前台",
        Foreground = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
        FontSize = 10.5,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 8, 0)
    };

    private UIElement CreateEndButton(BackgroundApp app)
    {
        var button = new Button
        {
            Content = "结束",
            Style = (Style)FindResource("LinkButton"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = app
        };

        button.Click += OnEndProcessClick;
        return button;
    }

    /// <summary>
    /// 结束进程要两步：托盘程序没有窗口，杀掉就是直接没了，误点代价太大。
    /// 第一次点把按钮变成「确认结束」，再点一次才真的动手。
    /// </summary>
    private void OnEndProcessClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BackgroundApp app } button) return;

        if (!_confirmEnd.Contains(app.Pid))
        {
            _confirmEnd.Add(app.Pid);
            button.Content = "确认结束";
            button.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5A));
            button.FontWeight = FontWeights.SemiBold;
            return;
        }

        var ok = BackgroundApps.End(app);
        _confirmEnd.Remove(app.Pid);

        ReloadBackground();

        if (!ok)
        {
            BackgroundHint.Text = $"结束 {app.Name} 失败（可能需要管理员权限）";
        }
    }

    private void OnBackgroundRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BackgroundApp app }) return;

        // 先收面板再切窗口：留着面板会挡视线，也还在参与「点外面就关」的判定
        RequestClose?.Invoke();

        BackgroundApps.Activate(app);
    }

    private FrameworkElement CreateAppIcon(BackgroundApp app)
    {
        var source = app.Icon ?? LoadIcon(app.Path);

        if (source is not null)
        {
            var image = new Image
            {
                Source = source,
                Width = 26,
                Height = 26,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };

            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        // 抠不到图标就给个首字母方块，总比空一块好
        return new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = app.Name.Length == 0 ? "?" : app.Name[..1].ToUpperInvariant(),
                Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private ImageSource? LoadIcon(string path)
    {
        if (path.Length == 0) return null;
        if (_iconCache.TryGetValue(path, out var cached)) return cached;

        var image = IconExtractor.FromExecutable(path);
        _iconCache[path] = image;
        return image;
    }

    // ---------------- 通用 ----------------

    private void ShowOnly(UIElement view)
    {
        WifiView.Visibility = ReferenceEquals(view, WifiView) ? Visibility.Visible : Visibility.Collapsed;
        WifiPasswordView.Visibility = ReferenceEquals(view, WifiPasswordView) ? Visibility.Visible : Visibility.Collapsed;
        VolumeView.Visibility = ReferenceEquals(view, VolumeView) ? Visibility.Visible : Visibility.Collapsed;
        KeyboardView.Visibility = ReferenceEquals(view, KeyboardView) ? Visibility.Visible : Visibility.Collapsed;
        BluetoothView.Visibility = ReferenceEquals(view, BluetoothView) ? Visibility.Visible : Visibility.Collapsed;
        BackgroundView.Visibility = ReferenceEquals(view, BackgroundView) ? Visibility.Visible : Visibility.Collapsed;

        // 离开音量页就别再每 150ms 读一次音量了
        if (!ReferenceEquals(view, VolumeView)) _volumeWatch.Stop();
    }

    /// <summary>面板尺寸变了之后重新摆一下音量条（第一次显示时 ActualWidth 还是 0）。</summary>
    public void RefreshVolumeLayout()
    {
        if (VolumeView.Visibility != Visibility.Visible) return;

        var volume = AudioService.GetVolume();
        if (volume >= 0) UpdateVolumeBar(volume);
    }
}
