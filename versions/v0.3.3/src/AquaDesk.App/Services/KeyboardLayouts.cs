using System.Globalization;
using System.Text;
using System.Windows.Media;
using AquaDesk.Core;
using Microsoft.Win32;

namespace AquaDesk.Services;

/// <summary>一个已安装的键盘布局，对应下拉框里的一行。</summary>
public sealed record KeyboardLayout(
    string LayoutId,
    ushort LangId,
    string Title,
    string Subtitle,
    ImageSource? Icon,
    string Badge,
    bool IsCurrent);

/// <summary>
/// 列出 / 切换已安装的键盘布局（等价于 Windows 那个「键盘布局」弹出层）。
///
/// 列表来自 <c>HKCU\Keyboard Layout\Preload</c>，每个值是 8 位 KLID，例如
/// <c>00000804</c>（简体中文）、<c>00000409</c>（美式键盘）。
///
/// 每行显示两段文字，和系统一致：
/// - 标题 = 语言的本地化名字（`GetLocaleInfo` + `LOCALE_SLOCALIZEDDISPLAYNAME`）
/// - 副标题 = 输入法名字（TSF 注册的 Description，如「搜狗拼音输入法」）；
///   纯键盘布局没有输入法，就取 `Layout Display Name`（间接字符串，要 `SHLoadIndirectString` 还原）
///
/// 切换走 `LoadKeyboardLayout` + `WM_INPUTLANGCHANGEREQUEST`，和原来那个中英切换是同一套，
/// 只是目标从写死的两个变成用户点的那一个。
/// </summary>
public static class KeyboardLayouts
{
    public static List<KeyboardLayout> GetAll()
    {
        var result = new List<KeyboardLayout>();

        try
        {
            var currentLangId = GetCurrentLangId();

            using var preload = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
            if (preload is null) return result;

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

                var profile = InputMethodIcon.FindProfile(langId);

                var title = LanguageName(langId);
                var subtitle = profile is not null && profile.Description.Length > 0
                    ? profile.Description
                    : KeyboardName(layoutId);

                result.Add(new KeyboardLayout(
                    layoutId,
                    langId,
                    title,
                    subtitle,
                    InputMethodIcon.GetIcon(langId, monochrome: false),
                    BadgeFor(langId),
                    langId == currentLangId));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 枚举键盘布局失败：{ex.Message}");
        }

        return result;
    }

    /// <summary>切到指定布局。窗口要接得住 WM_INPUTLANGCHANGEREQUEST，所以发给前台窗口。</summary>
    public static bool Activate(KeyboardLayout layout)
    {
        try
        {
            var hkl = NativeMethods.LoadKeyboardLayout(layout.LayoutId, NativeMethods.KLF_ACTIVATE);
            if (hkl == IntPtr.Zero) return false;

            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            NativeMethods.PostMessageW(hwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 切换键盘布局失败：{ex.Message}");
            return false;
        }
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
            _ => code
        };
    }
}
