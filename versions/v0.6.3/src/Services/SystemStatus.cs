using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AquaDesk.Services;

/// <summary>顶部菜单栏要显示的系统状态。全部走 Win32 / CoreAudio，不依赖第三方库。</summary>
public static class SystemStatus
{
    // ---------------- 电量 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public readonly record struct BatteryInfo(int Percent, bool IsCharging, bool HasBattery);

    public static BatteryInfo GetBattery()
    {
        if (!GetSystemPowerStatus(out var status)) return new BatteryInfo(100, false, false);

        var hasBattery = status.BatteryFlag != 128 && status.BatteryLifePercent != 255;
        var percent = status.BatteryLifePercent == 255 ? 100 : status.BatteryLifePercent;

        // BatteryFlag 的第 3 位是「正在充电」，ACLineStatus=1 表示接了电源
        var charging = (status.BatteryFlag & 8) != 0 || status.ACLineStatus == 1;

        return new BatteryInfo(percent, charging, hasBattery);
    }

    // ---------------- WiFi ----------------

    public static bool IsWifiConnected()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                return true;
            }
        }
        catch
        {
            // 拿不到就当没连
        }

        return false;
    }

    // ---------------- 音量（CoreAudio） ----------------

    /// <summary>
    /// 返回 0~100；拿不到返回 -1。
    ///
    /// 这里不再自己写一套 COM：早先那个 <c>[ComImport] class MMDeviceEnumeratorComObject</c> 的写法
    /// 在 .NET 8 上转换会静默失败（进程内 COM 类不能靠 ComImport 声明），导致永远返回 -1、
    /// 菜单栏音量图标永远是暗的。<see cref="AudioService"/> 那边已经改用
    /// <c>Type.GetTypeFromCLSID</c> + <c>Activator.CreateInstance</c>，并且带 <c>waveOut</c> 兜底，
    /// 统一从那儿取，别再分叉。
    /// </summary>
    public static int GetVolume() => AudioService.GetVolume();

    // ---------------- 输入法 ----------------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string pwszKlid, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const uint KLF_ACTIVATE = 0x00000001;

    /// <summary>在中英文两个键盘布局之间切换。</summary>
    public static void ToggleInputLanguage()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            var target = GetInputLanguage() == "中" ? "00000409" : "00000804";

            var layout = LoadKeyboardLayout(target, KLF_ACTIVATE);
            if (layout == IntPtr.Zero) return;

            PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layout);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 切换输入法失败：{ex.Message}");
        }
    }

    /// <summary>中文输入法返回 "中"，其它返回 "EN"。</summary>
    public static string GetInputLanguage()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            var threadId = GetWindowThreadProcessId(hwnd, out _);
            var layout = GetKeyboardLayout(threadId);

            var langId = (ushort)(layout.ToInt64() & 0xFFFF);

            // 0x0804 简体中文，0x0404 繁体，0x0C04 香港
            return langId is 0x0804 or 0x0404 or 0x0C04 ? "中" : "EN";
        }
        catch
        {
            return "EN";
        }
    }

    // ---------------- 前台应用 ----------------

    public static string GetForegroundAppName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Finder";

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "Finder";

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "Finder";
        }
    }
}
