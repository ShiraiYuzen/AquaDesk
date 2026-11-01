using System.Runtime.InteropServices;

namespace AquaDesk.Services;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// CoreAudio 的薄封装：枚举播放/录音设备、读写主音量。
/// 切换默认设备需要未公开的 IPolicyConfig，这里不做，只负责展示和调音量。
/// </summary>
public static class AudioService
{
    private const int EDataFlowRender = 0;
    private const int EDataFlowCapture = 1;
    private const int ERoleMultimedia = 1;
    private const int DeviceStateActive = 0x1;
    private const int ClsCtxInprocServer = 1;

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid);
        if (type is null) return null;

        return Activator.CreateInstance(type) as IMMDeviceEnumerator;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);

        int Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        int OpenPropertyStore(int access, out IPropertyStore store);

        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        int GetState(out int state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out int count);

        int GetAt(int index, out PROPERTYKEY key);

        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

        int Commit();
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out int channelCount);
        int SetMasterVolumeLevel(float levelDb, Guid context);
        int SetMasterVolumeLevelScalar(float level, Guid context);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
    }

    // 未公开接口：切换默认播放/录音设备只能走它
    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class CPolicyConfigClient
    {
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr format);
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, bool isDefault, IntPtr format);
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName);
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr endpointFormat, IntPtr mixFormat);
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr period);
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, bool fxStore, ref PROPERTYKEY key, IntPtr value);
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, bool fxStore, ref PROPERTYKEY key, IntPtr value);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, int role);
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceName, bool visible);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    // x64 上 PROPVARIANT 是 24 字节，Size 写小了 GetValue 会写越界
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT variant);

    // 音量兜底：CoreAudio 在某些机器上 Activate 会失败，winmm 这套老 API 反而稳
    [DllImport("winmm.dll")]
    private static extern int waveOutGetVolume(IntPtr device, out uint volume);

    [DllImport("winmm.dll")]
    private static extern int waveOutSetVolume(IntPtr device, uint volume);

    private static PROPERTYKEY FriendlyNameKey => new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    public static IReadOnlyList<AudioDevice> GetDevices(bool input)
    {
        var result = new List<AudioDevice>();
        object? enumeratorObject = null;

        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null) return result;
            enumeratorObject = enumerator;

            var flow = input ? EDataFlowCapture : EDataFlowRender;

            var defaultId = enumerator.GetDefaultAudioEndpoint(flow, ERoleMultimedia, out var defaultDevice) == 0
                ? GetDeviceId(defaultDevice)
                : null;

            if (enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out var collection) != 0) return result;

            collection.GetCount(out var count);

            for (var i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;

                var id = GetDeviceId(device);
                var name = GetFriendlyName(device);

                result.Add(new AudioDevice(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 枚举音频设备失败：{ex.Message}");
        }
        finally
        {
            if (enumeratorObject is not null && Marshal.IsComObject(enumeratorObject))
            {
                Marshal.ReleaseComObject(enumeratorObject);
            }
        }

        return result;
    }


    /// <summary>把某个设备设为默认（控制台 / 多媒体 / 通信 三个角色一起设）。</summary>
    public static bool SetDefaultDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        object? policyObject = null;

        try
        {
            var policy = (IPolicyConfig)new CPolicyConfigClient();
            policyObject = policy;

            var ok = false;
            for (var role = 0; role <= 2; role++)
            {
                if (policy.SetDefaultEndpoint(deviceId, role) == 0) ok = true;
            }

            return ok;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 切换默认音频设备失败：{ex.Message}");
            return false;
        }
        finally
        {
            if (policyObject is not null && Marshal.IsComObject(policyObject))
            {
                Marshal.ReleaseComObject(policyObject);
            }
        }
    }

    public static int GetVolume()
    {
        object? volumeObject = null;

        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null) return -1;

            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out var device) == 0)
            {
                var iid = typeof(IAudioEndpointVolume).GUID;

                if (device.Activate(ref iid, ClsCtxInprocServer, IntPtr.Zero, out var instance) == 0)
                {
                    volumeObject = instance;

                    var volume = (IAudioEndpointVolume)instance;
                    if (volume.GetMasterVolumeLevelScalar(out var level) == 0)
                    {
                        return (int)Math.Round(level * 100);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 读音量失败：{ex.Message}");
        }
        finally
        {
            if (volumeObject is not null && Marshal.IsComObject(volumeObject))
            {
                Marshal.ReleaseComObject(volumeObject);
            }
        }

        return GetVolumeFallback();
    }

    private static int GetVolumeFallback()
    {
        try
        {
            if (waveOutGetVolume(IntPtr.Zero, out var raw) != 0) return -1;

            var left = raw & 0xFFFF;
            return (int)Math.Round(left * 100.0 / 0xFFFF);
        }
        catch
        {
            return -1;
        }
    }

    public static bool SetVolume(int percent)
    {
        object? volumeObject = null;
        var applied = false;

        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null) return false;

            if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out var device) == 0)
            {
                var iid = typeof(IAudioEndpointVolume).GUID;

                if (device.Activate(ref iid, ClsCtxInprocServer, IntPtr.Zero, out var instance) == 0)
                {
                    volumeObject = instance;

                    var volume = (IAudioEndpointVolume)instance;
                    var level = NetFxShims.Clamp(percent, 0, 100) / 100f;

                    applied = volume.SetMasterVolumeLevelScalar(level, Guid.Empty) == 0;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 设音量失败：{ex.Message}");
        }
        finally
        {
            if (volumeObject is not null && Marshal.IsComObject(volumeObject))
            {
                Marshal.ReleaseComObject(volumeObject);
            }
        }

        if (applied) return true;

        try
        {
            var clamped = NetFxShims.Clamp(percent, 0, 100);
            var raw = (uint)(clamped * 0xFFFF / 100);
            return waveOutSetVolume(IntPtr.Zero, raw | (raw << 16)) == 0;
        }
        catch
        {
            return false;
        }
    }


    private static string GetDeviceId(IMMDevice device)
    {
        try
        {
            return device.GetId(out var id) == 0 ? id : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;

        try
        {
            if (device.OpenPropertyStore(0, out var propertyStore) != 0) return "未知设备";
            store = propertyStore;

            var key = FriendlyNameKey;
            if (propertyStore.GetValue(ref key, out var value) != 0) return "未知设备";

            var name = value.vt == 31 && value.pointerValue != IntPtr.Zero
                ? Marshal.PtrToStringUni(value.pointerValue)
                : null;

            PropVariantClear(ref value);

            return string.IsNullOrWhiteSpace(name) ? "未知设备" : name;
        }
        catch
        {
            return "未知设备";
        }
        finally
        {
            if (store is not null && Marshal.IsComObject(store)) Marshal.ReleaseComObject(store);
        }
    }
}
