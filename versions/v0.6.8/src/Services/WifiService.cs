using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AquaDesk.Services;

public sealed record WifiNetwork(
    string Ssid,
    int Signal,
    bool Secured,
    bool Connected,
    bool HasProfile);

/// <summary>一次查询的完整结果：网络列表 + 当前连接 + 当前信号。</summary>
public sealed record WifiSnapshot(
    IReadOnlyList<WifiNetwork> Networks,
    string? ConnectedSsid,
    int Signal);

/// <summary>
/// Wi-Fi 状态查询与连接。
///
/// 查询一律走 <c>wlanapi.dll</c>：<c>netsh wlan show networks</c> 的输出编码跟着
/// 父进程控制台代码页走，在中文系统上既可能是 UTF-8 也可能是 GBK，解码一错中文 SSID
/// 就成乱码；而且它每次要起一个进程，几百毫秒，扛不住每秒刷新的菜单栏图标。
///
/// 这里刻意**不**用 <c>Marshal.PtrToStructure</c> 去封送 WLAN 结构体 ——
/// 里面既有变长数组又有按字节对齐的 DOT11_MAC_ADDRESS，字段一错位就读到垃圾数据
/// （这正是之前中文乱码、信号恒为 0 的根因）。改为按 x64 的固定字节偏移直接读，
/// 偏移量取自 wlanapi.h 的结构定义，稳定且可验证。
///
/// <c>netsh</c> 只在「添加配置文件 / 发起连接」这两个写操作上使用，
/// 而且完全不解析它的输出：连没连上看的是 <see cref="Query"/> 的真实连接状态。
/// </summary>
public static class WifiService
{
    // ---------------- WLAN 结构的字节偏移（x64） ----------------

    // WLAN_INTERFACE_INFO = GUID(16) + WCHAR[256](512) + WLAN_INTERFACE_STATE(4)
    private const int InterfaceInfoSize = 532;

    // WLAN_AVAILABLE_NETWORK 总长 628，这里只用到几个字段
    private const int NetworkSize = 628;
    private const int OffSsidLen = 512;    // ULONG uSSIDLength
    private const int OffSsidBytes = 516;  // UCHAR ucSSID[32]
    private const int OffSignal = 604;     // WLAN_SIGNAL_QUALITY wlanSignalQuality
    private const int OffSecured = 608;    // BOOL bSecurityEnabled

    // WLAN_CONNECTION_ATTRIBUTES
    private const int OffConnState = 0;                 // WLAN_INTERFACE_STATE
    private const int OffConnProfile = 8;               // WCHAR[256] strProfileName
    private const int OffConnAssoc = OffConnProfile + 512; // WLAN_ASSOCIATION_ATTRIBUTES
    private const int OffConnSignal = OffConnAssoc + 60;   // wlanSignalQuality

    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeRadioState = 4;
    private const int InterfaceStateConnected = 1;

    // WLAN_RADIO_STATE = DWORD dwNumberOfPhys + WLAN_PHY_RADIO_STATE[64]
    // WLAN_PHY_RADIO_STATE = dwPhyIndex + 软件状态 + 硬件状态，各 4 字节
    private const int RadioStateSize = 4 + 64 * 12;
    private const int RadioStateOn = 1;
    private const int RadioStateOff = 2;

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(int version, IntPtr reserved, out int negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetAvailableNetworkList(
        IntPtr handle, ref Guid interfaceGuid, int flags, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(
        IntPtr handle,
        ref Guid interfaceGuid,
        int opcode,
        IntPtr reserved,
        out int size,
        out IntPtr data,
        out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern int WlanSetInterface(
        IntPtr handle,
        ref Guid interfaceGuid,
        int opcode,
        int size,
        IntPtr data,
        IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    // ---------------- 查询 ----------------

    private static readonly object QueryLock = new();
    private static WifiSnapshot _cache = new(Array.Empty<WifiNetwork>(), null, -1);
    private static DateTime _cacheAt = DateTime.MinValue;

    /// <summary>
    /// 查一次 Wi-Fi 状态。默认复用 4 秒内的缓存 —— 菜单栏图标每秒刷一次，
    /// 每次都真的去开句柄没必要；<paramref name="force"/> 用于打开面板 / 连接后取最新值。
    /// </summary>
    public static WifiSnapshot Query(bool force = false)
    {
        lock (QueryLock)
        {
            if (!force && (DateTime.Now - _cacheAt).TotalSeconds < 4) return _cache;

            var snapshot = QueryCore();
            _cache = snapshot;
            _cacheAt = DateTime.Now;
            return snapshot;
        }
    }

    private static WifiSnapshot QueryCore()
    {
        var empty = new WifiSnapshot(Array.Empty<WifiNetwork>(), null, -1);

        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return empty;

        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var interfaceList) != 0) return empty;

            try
            {
                var count = Marshal.ReadInt32(interfaceList, 0);
                if (count <= 0) return empty;

                var fallback = empty;

                for (var i = 0; i < count; i++)
                {
                    var entry = interfaceList + 8 + i * InterfaceInfoSize;
                    var guid = new Guid(ReadBytes(entry, 16));

                    var connectedSsid = ReadConnectedSsid(handle, guid, out var currentSignal);
                    var networks = ReadAvailableNetworks(handle, guid, connectedSsid);

                    if (networks.Count == 0)
                    {
                        // 没扫到网络也把连接状态记下来，避免多网卡时被后面的空结果盖掉
                        if (connectedSsid is not null && fallback.ConnectedSsid is null)
                        {
                            fallback = new WifiSnapshot(networks, connectedSsid, currentSignal);
                        }

                        continue;
                    }

                    // 连接中的网络一定在列表里，信号以列表里的实时值为准
                    var signal = networks.FirstOrDefault(n => n.Connected)?.Signal ?? currentSignal;
                    if (signal is < 0 or > 100) signal = 0;

                    return new WifiSnapshot(networks, connectedSsid, connectedSsid is null ? -1 : signal);
                }

                return fallback;
            }
            finally
            {
                WlanFreeMemory(interfaceList);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static string? ReadConnectedSsid(IntPtr handle, Guid guid, out int signal)
    {
        signal = 0;

        // 未连接时这个调用直接返回 ERROR_INVALID_STATE，不用另外判断
        if (WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection,
                IntPtr.Zero, out _, out var data, out _) != 0)
        {
            return null;
        }

        try
        {
            if (Marshal.ReadInt32(data, OffConnState) != InterfaceStateConnected) return null;

            var length = Math.Clamp(Marshal.ReadInt32(data, OffConnAssoc), 0, 32);
            var ssid = length > 0
                ? Encoding.UTF8.GetString(ReadBytes(data + OffConnAssoc + 4, length))
                : null;

            var quality = Marshal.ReadInt32(data, OffConnSignal);
            signal = quality is >= 0 and <= 100 ? quality : 0;

            return string.IsNullOrEmpty(ssid) ? null : ssid;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static List<WifiNetwork> ReadAvailableNetworks(IntPtr handle, Guid guid, string? connectedSsid)
    {
        var result = new List<WifiNetwork>();

        // flags = 1 | 2：同时包含隐藏网络和 ad-hoc 配置
        if (WlanGetAvailableNetworkList(handle, ref guid, 3, IntPtr.Zero, out var list) != 0)
        {
            return result;
        }

        try
        {
            var count = Marshal.ReadInt32(list, 0);

            for (var i = 0; i < count; i++)
            {
                var item = list + 8 + i * NetworkSize;

                var length = Math.Clamp(Marshal.ReadInt32(item, OffSsidLen), 0, 32);
                if (length == 0) continue;

                var ssid = Encoding.UTF8.GetString(ReadBytes(item + OffSsidBytes, length));
                if (string.IsNullOrEmpty(ssid)) continue;

                var signal = Math.Clamp(Marshal.ReadInt32(item, OffSignal), 0, 100);
                var secured = Marshal.ReadInt32(item, OffSecured) != 0;

                // strProfileName 非空 = 本机存过这个网络的配置，连接时不需要再问密码
                var profile = Marshal.PtrToStringUni(item) ?? string.Empty;

                result.Add(new WifiNetwork(
                    ssid,
                    signal,
                    secured,
                    string.Equals(ssid, connectedSsid, StringComparison.Ordinal),
                    profile.Length > 0));
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        // 同一个 SSID 可能有多个 AP / 多个频段，合并成一条并取最强信号
        return result
            .GroupBy(n => n.Ssid, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(n => n.Signal).First())
            .OrderByDescending(n => n.Connected)
            .ThenByDescending(n => n.Signal)
            .ToList();
    }

    private static byte[] ReadBytes(IntPtr address, int count)
    {
        var buffer = new byte[count];
        Marshal.Copy(address, buffer, 0, count);
        return buffer;
    }

    // ---------------- 兼容旧调用的薄封装 ----------------

    /// <summary>扫描一次网络列表（强制刷新缓存）。</summary>
    public static IReadOnlyList<WifiNetwork> GetNetworks() => Query(true).Networks;

    /// <summary>当前连接的 SSID，未连接返回 null。</summary>
    public static string? GetConnectedSsid() => Query().ConnectedSsid;

    /// <summary>菜单栏图标用的信号强度；未连接返回 -1。</summary>
    public static int GetCachedSignal()
    {
        var snapshot = Query();
        return snapshot.ConnectedSsid is null ? -1 : snapshot.Signal;
    }

    /// <summary>这个网络是不是已经保存过配置。</summary>
    public static bool HasProfile(string ssid) =>
        Query().Networks.Any(n => string.Equals(n.Ssid, ssid, StringComparison.Ordinal) && n.HasProfile);

    // ---------------- 射频开关 / 断开 ----------------

    /// <summary>Wi-Fi 射频是不是开着。没有无线网卡也返回 false。</summary>
    public static bool IsRadioOn()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return false;

        try
        {
            if (!TryGetInterface(handle, out var guid)) return false;

            if (WlanQueryInterface(handle, ref guid, OpcodeRadioState, IntPtr.Zero, out var size, out var data, out _) != 0)
            {
                return false;
            }

            try
            {
                // phy0 的软件状态：1 = 开，2 = 关
                return size >= 12 && Marshal.ReadInt32(data, 8) == RadioStateOn;
            }
            finally
            {
                WlanFreeMemory(data);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    /// <summary>
    /// 开关 Wi-Fi。
    ///
    /// **主路径是 Radio Manager（<c>Windows.Devices.Radios</c>）**——Win11 自己的 Wi-Fi 开关用的就是它，
    /// 不需要管理员权限。旧的 <c>WlanSetInterface</c> 射频接口在 Killer 这类网卡上**即使提权也返回 87**
    /// （实测：管理员=True，rc=87，参数尺寸 772 和查询结果完全一致），所以只能当兜底。
    ///
    /// 三级：
    /// 1. Radio Manager（推荐，无需提权）
    /// 2. <c>WlanSetInterface</c>（部分网卡/驱动支持）
    /// 3. 以管理员身份重新拉起自己再试一次（会弹 UAC，前两条都失败时才走）
    /// </summary>
    public static bool SetRadioOn(bool on)
    {
        if (SetRadioWithoutElevation(on)) return true;

        return RunElevated(on);
    }

    /// <summary>
    /// 不开新的提权进程，能试的都试一遍。
    /// 提权子进程必须用这个（用 <see cref="SetRadioOn"/> 会无限递归）。
    /// </summary>
    public static bool SetRadioWithoutElevation(bool on) =>
        SetRadioViaRadioManager(on) || SetRadioDirect(on);

    /// <summary>用 Radio Manager 开关 Wi-Fi。这是 Win11 设置里那个开关的同款接口。</summary>
    private static bool SetRadioViaRadioManager(bool on)
    {
        try
        {
            // WinRT 的异步操作不能直接阻塞 UI 线程等，放线程池上跑
            var task = Task.Run(() => SetRadioViaRadioManagerAsync(on));

            return task.Wait(8000) && task.Result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] Radio Manager 开关失败：{ex.Message}");
            return false;
        }
    }

    private static async Task<bool> SetRadioViaRadioManagerAsync(bool on)
    {
        var access = await Windows.Devices.Radios.Radio.RequestAccessAsync();
        if (access != Windows.Devices.Radios.RadioAccessStatus.Allowed) return false;

        var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();

        var wifi = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.WiFi);
        if (wifi is null) return false;

        var result = await wifi.SetStateAsync(on
            ? Windows.Devices.Radios.RadioState.On
            : Windows.Devices.Radios.RadioState.Off);

        return result == Windows.Devices.Radios.RadioAccessStatus.Allowed;
    }

    /// <summary>直接改射频状态。需要管理员权限，提权后的子进程也走这条。</summary>
    public static bool SetRadioDirect(bool on)
    {
        var rc = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (rc != 0)
        {
            Trace($"  WlanOpenHandle rc={rc}");
            return false;
        }

        try
        {
            if (!TryGetInterface(handle, out var guid))
            {
                Trace("  找不到无线网卡");
                return false;
            }

            // 先读当前状态再改：硬件状态是只读的，自己拼结构体会被拒
            rc = WlanQueryInterface(handle, ref guid, OpcodeRadioState, IntPtr.Zero, out var size, out var data, out _);
            if (rc != 0)
            {
                Trace($"  WlanQueryInterface(radio) rc={rc}");
                return false;
            }

            var state = new byte[RadioStateSize];
            var count = 0;

            try
            {
                Marshal.Copy(data, state, 0, Math.Min(size, RadioStateSize));
                count = BitConverter.ToInt32(state, 0);
            }
            finally
            {
                WlanFreeMemory(data);
            }

            Trace($"  查询 size={size} phys={count} phy0软={BitConverter.ToInt32(state, 8)}");

            if (count <= 0) return false;

            for (var i = 0; i < count && i < 64; i++)
            {
                BitConverter.GetBytes(on ? RadioStateOn : RadioStateOff).CopyTo(state, 4 + i * 12 + 4);
            }

            var buffer = Marshal.AllocHGlobal(state.Length);

            try
            {
                Marshal.Copy(state, 0, buffer, state.Length);
                rc = WlanSetInterface(handle, ref guid, OpcodeRadioState, state.Length, buffer, IntPtr.Zero);
                Trace($"  WlanSetInterface rc={rc}（size={state.Length}）");
                return rc == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static void Trace(string message) => App.Log(message, null);

    /// <summary>以管理员身份重新拉起自己，只执行一次射频开关。</summary>
    private static bool RunElevated(bool on)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            var startInfo = new ProcessStartInfo(exe, on ? "--wifi-on" : "--wifi-off")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            if (!process.WaitForExit(20000)) return false;

            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户在 UAC 弹窗里点了「否」
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 提权切换 Wi-Fi 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>断开当前 Wi-Fi（netsh 这条命令不需要提权）。</summary>
    public static void Disconnect() => RunNetsh("wlan disconnect");

    private static bool TryGetInterface(IntPtr handle, out Guid guid)
    {
        guid = Guid.Empty;

        if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return false;

        try
        {
            if (Marshal.ReadInt32(list, 0) <= 0) return false;

            var bytes = new byte[16];
            Marshal.Copy(list + 8, bytes, 0, 16);
            guid = new Guid(bytes);
            return true;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    // ---------------- 连接 ----------------

    /// <summary>连接到一个网络。密码为空表示开放网络或已保存过配置。</summary>
    public static bool Connect(string ssid, string? password)
    {
        var known = Query(true).Networks
            .FirstOrDefault(n => string.Equals(n.Ssid, ssid, StringComparison.Ordinal));

        if (known is null || !known.HasProfile)
        {
            if (string.IsNullOrEmpty(password)) return false;

            var profilePath = Path.Combine(Path.GetTempPath(), $"aquadesk-wifi-{Guid.NewGuid():N}.xml");

            try
            {
                // 不带 BOM：netsh 对 BOM 的容忍度因版本而异，裸 UTF-8 最稳
                File.WriteAllText(profilePath, BuildProfileXml(ssid, password), new UTF8Encoding(false));
                RunNetsh($"wlan add profile filename=\"{profilePath}\" user=all");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AquaDesk] 写入 Wi-Fi 配置失败：{ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(profilePath)) File.Delete(profilePath);
                }
                catch
                {
                    // 临时文件删不掉无所谓
                }
            }
        }

        RunNetsh($"wlan connect name=\"{ssid}\" ssid=\"{ssid}\"");

        // 不解析 netsh 的输出（编码不可靠、中文关键词还会随语言变），
        // 直接轮询真实连接状态：连上了 wlanapi 立刻就能读到
        for (var i = 0; i < 16; i++)
        {
            Thread.Sleep(400);
            if (string.Equals(Query(true).ConnectedSsid, ssid, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string BuildProfileXml(string ssid, string password)
    {
        var escapedSsid = System.Security.SecurityElement.Escape(ssid) ?? ssid;
        var escapedKey = System.Security.SecurityElement.Escape(password) ?? password;

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
    <name>{escapedSsid}</name>
    <SSIDConfig>
        <SSID>
            <name>{escapedSsid}</name>
        </SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>auto</connectionMode>
    <MSM>
        <security>
            <authEncryption>
                <authentication>WPA2PSK</authentication>
                <encryption>AES</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
            <sharedKey>
                <keyType>passPhrase</keyType>
                <protected>false</protected>
                <keyMaterial>{escapedKey}</keyMaterial>
            </sharedKey>
        </security>
    </MSM>
</WLANProfile>
""";
    }

    /// <summary>
    /// 跑一条 netsh 命令。输出用异步方式读干净再等退出 ——
    /// 只重定向不读，管道缓冲满了 netsh 会永久卡住。
    /// </summary>
    private static int RunNetsh(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null) return -1;

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(10000))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // 已经退出了就无所谓
                }

                return -1;
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] netsh 调用失败：{ex.Message}");
            return -1;
        }
    }
}
