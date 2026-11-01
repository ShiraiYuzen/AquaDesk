using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using AquaDesk;
using AquaDesk.Core;
using Microsoft.Win32;

namespace AquaDesk.Services;

/// <summary>下拉框里的一行：一个输入法，或者一个纯键盘布局。</summary>
public sealed record KeyboardLayout(
    string LayoutId,
    ushort LangId,
    string Clsid,
    string ProfileGuid,
    string Title,
    string Subtitle,
    ImageSource? Icon,
    string Badge,
    bool IsCurrent)
{
    /// <summary>TSF 注册的输入法（搜狗 / 讯飞 / 微信 / 微软拼音…）。false 表示纯键盘布局。</summary>
    public bool IsInputMethod => Clsid.Length > 0 && ProfileGuid.Length > 0;
}

/// <summary>
/// 列出 / 切换输入法与键盘布局（等价于 Windows 那个「键盘布局」弹出层，但列得更全）。
///
/// 两套数据源，缺一不可：
/// 1. <c>HKCU\Keyboard Layout\Preload</c> —— 装了哪些**语言**，比如 0409 英语、0804 简体中文。
/// 2. <c>HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile\0x{langid}\{GUID}</c>
///    —— 这个语言下装了哪些**输入法**，每家一个 GUID，还各自带 IconFile/IconIndex。
///
/// 老实现只取「第一个带图标的 profile」，所以中文永远显示搜狗，讯飞 / 微信 /
/// 微软拼音全被吞掉了。现在按语言把 profile 全部展开，一个输入法一行。
///
/// 切换分两层：
/// - 语言不同 → <c>LoadKeyboardLayout</c> + <c>WM_INPUTLANGCHANGEREQUEST</c>（和各输入法共用 HKL，够用）
/// - 同一语言下换输入法 → 只能走 TSF 的 <c>ActivateLanguageProfile</c>。
///   这一层是必须的：中文下搜狗 / 讯飞 / 微软拼音的 HKL **全都是 00000804**，
///   光发 WM_INPUTLANGCHANGEREQUEST 切不动（实测 HKL = 0x08040804）。
/// </summary>
public static class KeyboardLayouts
{
    /// <summary>面板打开前记录的用户真实输入目标窗口。见 <see cref="CaptureSwitchTarget"/>。</summary>
    private static IntPtr _switchTarget;

    /// <summary>
    /// 用户最后一次在面板里点过的输入法。TSF 没有可用的「读当前 profile」接口
    /// （GetActiveLanguageProfile 在本机直接 AV），所以「哪个输入法在生效」只能靠记。
    /// 语言层面仍然是实时读 GetKeyboardLayout，不会错。
    /// </summary>
    private static string _lastProfileKey = string.Empty;

    public static List<KeyboardLayout> GetAll()
    {
        var result = new List<KeyboardLayout>();

        try
        {
            var currentLangId = GetCurrentLangId();

            foreach (var (layoutId, langId) in PreloadLanguages())
            {
                var profiles = InputMethodIcon.FindProfiles(langId);

                if (profiles.Count == 0)
                {
                    // 纯键盘布局（英语-美式键盘这类）：没有 TSF profile，只有 Layout Display Name
                    const string title = "";   // 下面统一算语言名
                    result.Add(new KeyboardLayout(
                        layoutId,
                        langId,
                        string.Empty,
                        string.Empty,
                        LanguageName(langId),
                        KeyboardName(layoutId),
                        InputMethodIcon.Generated(langId, LanguageName(langId), BadgeFor(langId)),
                        BadgeFor(langId),
                        langId == currentLangId && _lastProfileKey.Length == 0));

                    _ = title;
                    continue;
                }

                // 同一语言下有多个输入法时，靠记忆里的那条打勾；没有记忆就先认第一个
                var activeKey = _lastProfileKey;
                var fallback = profiles[0].Key;

                foreach (var profile in profiles)
                {
                    var key = profile.Key;
                    var isCurrent = langId == currentLangId
                                    && string.Equals(key, activeKey.Length > 0 ? activeKey : fallback,
                                        StringComparison.OrdinalIgnoreCase);

                    result.Add(new KeyboardLayout(
                        layoutId,
                        langId,
                        profile.Clsid,
                        profile.Guid,
                        LanguageName(langId),
                        profile.Description.Length > 0 ? profile.Description : KeyboardName(layoutId),
                        InputMethodIcon.GetIcon(profile, monochrome: false)
                            ?? InputMethodIcon.Generated(langId, profile.Description, BadgeFor(langId)),
                        BadgeFor(langId),
                        isCurrent));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 枚举输入法失败：{ex.Message}");
        }

        return result;
    }

    /// <summary>当前语言下应该显示在菜单栏上的输入法（靠记忆，取不到就退回第一个）。</summary>
    public static TsfProfile? CurrentProfileHint(ushort langId)
    {
        var profiles = InputMethodIcon.FindProfiles(langId);
        if (profiles.Count == 0) return null;

        if (_lastProfileKey.Length > 0)
        {
            foreach (var profile in profiles)
            {
                if (string.Equals(profile.Key, _lastProfileKey, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
        }

        return profiles[0];
    }

    /// <summary>
    /// 面板打开前记录的用户真实输入目标窗口。切换请求要发给它，而不是发给我们自己。
    ///
    /// 踩过的坑：点胶囊图标 → 弹出面板 → 点面板里的行，这一串里第二步会把键盘焦点
    /// 抢到面板的 Popup 上（Popup 不是 NOACTIVATE 窗口），现场再调 GetForegroundWindow()
    /// 拿到的就是我们自己，把 WM_INPUTLANGCHANGEREQUEST 发给自己等于没切。
    ///
    /// WM_INPUTLANGCHANGEREQUEST 按微软文档是发给「焦点窗口」的，不是前台窗口——
    /// 所以这里记的是焦点窗口，拿不到才退回前台窗口。
    /// </summary>
    public static void CaptureSwitchTarget()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var focus = NativeMethods.GetFocusedWindowOf(threadId);
        _switchTarget = focus != IntPtr.Zero ? focus : hwnd;
    }

    /// <summary>切到指定输入法 / 布局。</summary>
    public static bool Activate(KeyboardLayout layout)
    {
        try
        {
            // 第一层：同一语言下换输入法，必须走 TSF（HKL 是同一个，发消息切不动）
            var tsfLabel = "-";
            var profileOk = true;

            if (layout.IsInputMethod)
            {
                profileOk = ActivateProfile(layout.Clsid, layout.LangId, layout.ProfileGuid);
                tsfLabel = profileOk ? "ok" : "fail";

                if (profileOk)
                {
                    _lastProfileKey = layout.Clsid + "/" + layout.ProfileGuid;
                }
            }
            else
            {
                _lastProfileKey = string.Empty;
            }

            // 第二层：语言不同时还得把 HKL 换过去（同语言时这步等于原地不动）
            var hkl = NativeMethods.LoadKeyboardLayout(layout.LayoutId, NativeMethods.KLF_ACTIVATE);
            if (hkl == IntPtr.Zero)
            {
                App.Log($"切换键盘布局失败：LoadKeyboardLayout({layout.LayoutId}) 返回 0", null);
                return false;
            }

            var hwnd = _switchTarget != IntPtr.Zero ? _switchTarget : NativeMethods.GetForegroundWindow();
            App.Log($"切换输入法：{layout.Subtitle}（{layout.LayoutId}，TSF={tsfLabel}）" +
                    $" hkl=0x{hkl:X} 目标=0x{hwnd:X}", null);

            if (hwnd == IntPtr.Zero) return false;

            // wParam=1（bLangInSystemCharset=TRUE）：中文这类 DBCS 能用系统字符集表达。
            // 传 0 会被某些依赖系统字体的编辑器当作「不支持」而拒绝切换请求。
            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, new IntPtr(1), hkl);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 切换输入法失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 用 TSF 激活某个输入法。
    ///
    /// 这里**没有**用 ComImport 接口：同一个 CLSID 在控制台程序里 cast 成功，
    /// 在 WPF 进程里却抛 InvalidCastException(E_NOINTERFACE)，大概是 WPF 的 STA/OLE
    /// 初始化让 CoCreateInstance 走了代理路径。改成手动取 vtable 直接调函数指针，
    /// 绕开 CLR 的接口查询，两种进程里行为一致。
    ///
    /// 槽位：IUnknown 占 0-2，Register 3，Unregister 4，AddLanguageProfile 5，
    /// RemoveLanguageProfile 6，EnumInputProcessorInfo 7，GetDefaultLanguageProfile 8，
    /// SetDefaultLanguageProfile 9，ActivateLanguageProfile 10。
    ///
    /// 另外记一笔：同一个接口的 GetActiveLanguageProfile（槽 11）在 Win11 上直接
    /// AccessViolation 崩进程，所以「读当前是哪个 profile」这件事不做，改用记忆。
    /// </summary>
    private static bool ActivateProfile(string clsid, ushort langId, string profileGuid) =>
        ActivateProfileCore(clsid, langId, profileGuid);

    private static bool ActivateProfileCore(string clsid, ushort langId, string profileGuid)
    {
        try
        {
            // 用 msctf.dll 导出的工厂函数，不走 CoCreateInstance：
            // 在 WPF 进程里 CoCreateInstance(CLSID_TF_InputProcessorProfiles) 拿到的对象
            // 对 IID_ITfInputProcessorProfiles 一律回 E_NOINTERFACE（0x80004002），
            // 而同样的代码在控制台进程里成功 —— 大概是 WPF 已经初始化过 TSF，
            // 类工厂那边走了另一条路。TF_Create* 直接在进程内 new 一个，没有这层问题。
            var hr = TF_CreateInputProcessorProfiles(out var profilesPtr);
            if (hr < 0 || profilesPtr == IntPtr.Zero)
            {
                App.Log($"TSF 激活输入法失败：TF_CreateInputProcessorProfiles 0x{hr:X8}", null);
                return false;
            }

            try
            {
                var clsidValue = new Guid(clsid);
                var guidValue = new Guid(profileGuid);

                var vtable = Marshal.ReadIntPtr(profilesPtr);
                var entry = Marshal.ReadIntPtr(vtable, 10 * IntPtr.Size);

                var activate = Marshal.GetDelegateForFunctionPointer<ActivateLanguageProfileFn>(entry);

                hr = activate(profilesPtr, ref clsidValue, langId, ref guidValue);

                if (hr < 0) App.Log($"TSF 激活输入法失败：ActivateLanguageProfile 0x{hr:X8}", null);

                return hr >= 0;
            }
            finally
            {
                Marshal.Release(profilesPtr);
            }
        }
        catch (Exception ex)
        {
            App.Log($"TSF 激活输入法异常：{ex.GetType().Name} 0x{ex.HResult:X8}", null);
            return false;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ActivateLanguageProfileFn(
        IntPtr self, ref Guid rclsid, ushort langid, ref Guid guidProfile);

    [DllImport("msctf.dll")]
    private static extern int TF_CreateInputProcessorProfiles(out IntPtr ppipr);

    /// <summary>Preload 里登记的语言：8 位 KLID + 语言 ID。</summary>
    private static List<(string LayoutId, ushort LangId)> PreloadLanguages()
    {
        var list = new List<(string, ushort)>();

        using var preload = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
        if (preload is null) return list;

        var names = preload.GetValueNames()
            .OrderBy(name => int.TryParse(name, out var index) ? index : int.MaxValue)
            .ToList();

        foreach (var name in names)
        {
            if (preload.GetValue(name) is not string raw) continue;

            var layoutId = raw.Trim();
            if (layoutId.Length != 8) continue;

            if (!ushort.TryParse(layoutId[^4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var langId))
            {
                continue;
            }

            // 同一语言可能被登记多次（少见），去重免得面板里出现两遍
            if (list.Any(item => item.Item2 == langId)) continue;

            list.Add((layoutId, langId));
        }

        return list;
    }

    private static ushort GetCurrentLangId()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var layout = NativeMethods.GetKeyboardLayout(threadId);

        return (ushort)(layout.ToInt64() & 0xFFFF);
    }

    private static string LanguageName(ushort langId)
    {
        var buffer = new StringBuilder(256);

        var length = NativeMethods.GetLocaleInfoW(
            langId, NativeMethods.LOCALE_SLOCALIZEDDISPLAYNAME, buffer, buffer.Capacity);

        return length > 0 ? buffer.ToString() : $"0x{langId:X4}";
    }

    private static string KeyboardName(string layoutId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{layoutId}");

            if (key is null) return string.Empty;

            // "Layout Display Name" 是 "@%SystemRoot%\system32\input.dll,-5000" 这种间接字符串
            if (key.GetValue("Layout Display Name") is string indirect)
            {
                var buffer = new StringBuilder(512);

                if (NativeMethods.SHLoadIndirectString(indirect, buffer, buffer.Capacity, IntPtr.Zero) == 0
                    && buffer.Length > 0)
                {
                    return buffer.ToString();
                }
            }

            return key.GetValue("Layout Text") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>没有图标的布局（美式键盘这类）用一个短代号占位。</summary>
    private static string BadgeFor(ushort langId)
    {
        var buffer = new StringBuilder(16);

        if (NativeMethods.GetLocaleInfoW(langId, NativeMethods.LOCALE_SISO639LANGNAME, buffer, buffer.Capacity) <= 0)
        {
            return "?";
        }

        var code = buffer.ToString().ToUpperInvariant();

        return code switch
        {
            "EN" => "ENG",
            "ZH" => "中",
            _ => code,
        };
    }
}
