using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace AquaDesk.Services;

/// <summary>蓝牙设备的一条记录。</summary>
public sealed record BluetoothDeviceInfo(string Id, string Name, bool Connected, bool Paired);

/// <summary>
/// 蓝牙状态与设备列表。
///
/// 全部走 WinRT：开关是 <see cref="Radio"/>（和 Wi-Fi 那个同一套，Win11 设置里的开关就是它），
/// 设备列表是 <see cref="DeviceInformation"/> + <see cref="BluetoothDevice"/>。
///
/// 注意：WinRT 的调用都是异步的，而 UI 线程不能阻塞等待，所以对外的同步方法
/// 一律用 <c>Task.Run(...).Wait()</c> 包一层，真正的异步实现放在 <c>*Async</c> 里。
/// </summary>
public static class BluetoothService
{
    private static DeviceWatcher? _watcher;

    /// <summary>发现到新设备（回调在后台线程，调用方自己切到 UI 线程）。</summary>
    public static event Action<BluetoothDeviceInfo>? DeviceFound;

    /// <summary>蓝牙无线电是不是开着。</summary>
    public static bool IsRadioOn()
    {
        try
        {
            var task = Task.Run(GetRadioStateAsync);
            return task.Wait(4000) && task.Result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 读蓝牙状态失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>开关蓝牙无线电。</summary>
    public static bool SetRadioOn(bool on)
    {
        try
        {
            var task = Task.Run(() => SetRadioStateAsync(on));
            return task.Wait(8000) && task.Result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 开关蓝牙失败：{ex.Message}");
            return false;
        }
    }

    private static async Task<bool> GetRadioStateAsync()
    {
        var radios = await Radio.GetRadiosAsync();
        var bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);

        return bluetooth is not null && bluetooth.State == RadioState.On;
    }

    private static async Task<bool> SetRadioStateAsync(bool on)
    {
        var access = await Radio.RequestAccessAsync();
        if (access != RadioAccessStatus.Allowed) return false;

        var radios = await Radio.GetRadiosAsync();
        var bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        if (bluetooth is null) return false;

        var result = await bluetooth.SetStateAsync(on ? RadioState.On : RadioState.Off);
        return result == RadioAccessStatus.Allowed;
    }

    /// <summary>已配对（保存过）的设备，附带当前连接状态。</summary>
    public static List<BluetoothDeviceInfo> GetPaired()
    {
        try
        {
            var task = Task.Run(GetPairedAsync);
            return task.Wait(6000) ? task.Result : new List<BluetoothDeviceInfo>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 读已配对设备失败：{ex.Message}");
            return new List<BluetoothDeviceInfo>();
        }
    }

    private static async Task<List<BluetoothDeviceInfo>> GetPairedAsync()
    {
        var result = new List<BluetoothDeviceInfo>();

        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector);

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Name)) continue;

            var connected = false;

            try
            {
                var bluetooth = await BluetoothDevice.FromIdAsync(device.Id);
                connected = bluetooth?.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
            catch
            {
                // 设备刚好离线，按未连接算
            }

            result.Add(new BluetoothDeviceInfo(device.Id, device.Name, connected, true));
        }

        // 已连接的排前面
        result.Sort((a, b) => b.Connected.CompareTo(a.Connected));

        return result;
    }

    /// <summary>系统缓存里的未配对设备（以前发现过的），不用等广播。</summary>
    public static List<BluetoothDeviceInfo> GetUnpairedCached()
    {
        try
        {
            var task = Task.Run(GetUnpairedCachedAsync);
            return task.Wait(6000) ? task.Result : new List<BluetoothDeviceInfo>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 读缓存设备失败：{ex.Message}");
            return new List<BluetoothDeviceInfo>();
        }
    }

    private static async Task<List<BluetoothDeviceInfo>> GetUnpairedCachedAsync()
    {
        var result = new List<BluetoothDeviceInfo>();

        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(false);
        var devices = await DeviceInformation.FindAllAsync(selector);

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Name)) continue;

            result.Add(new BluetoothDeviceInfo(device.Id, device.Name, false, false));
        }

        return result;
    }

    /// <summary>开始监听新出现的未配对设备（缓存那批由 <see cref="GetUnpairedCached"/> 单独取）。</summary>
    public static void StartDiscovery()
    {
        StopDiscovery();

        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(false);
            _watcher = DeviceInformation.CreateWatcher(selector);

            _watcher.Added += (_, device) =>
            {
                if (string.IsNullOrWhiteSpace(device.Name)) return;

                DeviceFound?.Invoke(new BluetoothDeviceInfo(device.Id, device.Name, false, false));
            };

            _watcher.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 搜索蓝牙设备失败：{ex.Message}");
        }
    }

    public static void StopDiscovery()
    {
        try
        {
            if (_watcher is null) return;

            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                _watcher.Stop();
            }

            _watcher = null;
        }
        catch
        {
            // 停不掉无所谓，进程退出会清掉
        }
    }

    /// <summary>配对（Windows 自己会弹确认框）。</summary>
    public static bool Pair(string id)
    {
        try
        {
            var task = Task.Run(() => PairAsync(id));
            return task.Wait(30000) && task.Result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 配对失败：{ex.Message}");
            return false;
        }
    }

    private static async Task<bool> PairAsync(string id)
    {
        var device = await DeviceInformation.CreateFromIdAsync(id);
        if (device is null) return false;

        var result = await device.Pairing.PairAsync();
        return result.Status is DevicePairingResultStatus.Paired
            or DevicePairingResultStatus.AlreadyPaired;
    }
}
