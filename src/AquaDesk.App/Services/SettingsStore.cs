using System.IO;
using System.Text.Json;
using AquaDesk.Models;

namespace AquaDesk.Services;

/// <summary>配置落盘位置：%AppData%\AquaDesk-net48\settings.json
/// （net48 实验构建用独立目录，和 .NET 8 主版本 %AppData%\AquaDesk 互不干扰）</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AquaDesk-net48");

    private static string FilePath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>
    /// 统一风格：没开「允许切换外观」时，界面固定用液态透明玻璃。
    ///
    /// 读配置时做一次，免得手改 JSON 或旧配置把界面停在某个设置页里根本看不到的样式上。
    /// 改完立刻落盘：只改内存的话，配置文件里还写着别的样式，对不上会让人以为没生效。
    /// </summary>
    internal static void EnforceLockedStyle(AppSettings settings)
    {
        if (settings.AllowStyleSwitch || settings.Style == Models.DockStyle.LiquidClear) return;

        settings.Style = Models.DockStyle.LiquidClear;
        Save(settings);
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            // 没解锁「允许切换外观」就统一用液态透明玻璃
            EnforceLockedStyle(settings);

            // 一次性迁移：0.3.5 之前的配置可能停在「液态透明玻璃」上，
            // 那是从旧版本点过的状态，不是用户这次的主动选择。统一回到默认的液态玻璃，
            // 之后再切换就按用户自己的来（SettingsVersion 已经写成 1）。
            //
            // 必须立刻落盘：只改内存的话，用户切到经典、重启、又被迁移拉回液态玻璃 ——
            // 表现就是「经典选了没用」。
            if (settings.SettingsVersion < 1)
            {
                settings.Style = Models.DockStyle.LiquidClear;
                settings.SettingsVersion = 1;
                Save(settings);
            }

            // 一次性迁移 2：Windows / AquaDesk 两个入口默认搬到状态胶囊，
            // Dock 上默认不再摆这两块。老配置里存着 true，不迁移的话它们会顽固地留在 Dock 上。
            if (settings.SettingsVersion < 2)
            {
                settings.ShowWindowsIcon = false;
                settings.ShowBrandIcon = false;
                settings.SettingsVersion = 2;
                Save(settings);
            }

            // 一次性迁移 3：这两个开关的含义又变回来了，但对象换成状态胶囊 ——
            // 0.4.9 起 Dock 上这两颗彻底不摆（DockViewModel.IsSystemEnabled 恒 false），
            // 开关只管胶囊上显不显示。上一轮迁移把它们置成了 false，
            // 不迁移的话升级后胶囊上就既没有设置、也没有电源入口了。
            if (settings.SettingsVersion < 3)
            {
                settings.ShowWindowsIcon = true;
                settings.ShowBrandIcon = true;
                settings.SettingsVersion = 3;
                Save(settings);
            }

            return settings;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 读取设置失败，用默认值：{ex.Message}");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings) =>
        WriteAtomic(FilePath, JsonSerializer.Serialize(settings, JsonOptions));

    /// <summary>
    /// 原子写：先写临时文件再整体替换。
    /// 直接覆盖写如果中途进程被杀，会留下半截 JSON，下次启动解析失败就当成「没有配置」重新初始化——
    /// 用户看到的就是「每次重启都恢复默认」。
    /// </summary>
    internal static void WriteAtomic(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = path + ".tmp";
            File.WriteAllText(temp, content);

            if (File.Exists(path))
            {
                // 只有现有文件是合法 JSON 才拿去做备份，
                // 否则会把坏文件覆盖到 .bak 上，把唯一一份好数据也毁掉。
                var backup = path + ".bak";
                try
                {
                    var existing = File.ReadAllText(path);
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        using var _ = JsonDocument.Parse(existing);
                        File.Copy(path, backup, overwrite: true);
                    }
                }
                catch
                {
                    // 现有文件本身就坏了：跳过备份，保留原来的 .bak
                }
            }

            NetFxShims.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 写入 {path} 失败：{ex.Message}");
        }
    }
}
