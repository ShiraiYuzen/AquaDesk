using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AquaDesk.Services;

/// <summary>
/// 电源操作，给 Dock 上的 Windows 菜单用。
///
/// 分工：
/// - 锁定 / 睡眠走 Win32，一个 API 调用就完事，不用起进程；
/// - 关机 / 重启交给系统自带的 <c>shutdown.exe</c>——自己调 <c>ExitWindowsEx</c> 得先
///   <c>AdjustTokenPrivileges</c> 打开 <c>SeShutdownPrivilege</c>，几十行代码换不来什么，
///   而且提权失败时的表现还不如系统工具明确。
/// </summary>
public static class PowerService
{
    public static bool Lock()
    {
        try
        {
            return LockWorkStation();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 锁定失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>睡眠（不是休眠）：hibernate 传 false，宁可失败也不要把机器睡进硬盘。</summary>
    public static bool Sleep()
    {
        try
        {
            return SetSuspendState(false, false, false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 睡眠失败：{ex.Message}");
            return false;
        }
    }

    public static bool Shutdown() => RunShutdown("/s /t 0");

    public static bool Restart() => RunShutdown("/r /t 0");

    private static bool RunShutdown(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return process is not null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 执行 shutdown {arguments} 失败：{ex.Message}");
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);
}
