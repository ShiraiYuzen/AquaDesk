using System.IO;
using System.Text.Json;
using AquaDesk.Models;

namespace AquaDesk.Services;

/// <summary>Dock 项落盘：%AppData%\AquaDesk\dock.json</summary>
public static class DockStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(SettingsStore.RootDirectory, "dock.json");
    private static string BackupPath => FilePath + ".bak";

    /// <summary>
    /// 返回 null 只代表「从来没有过配置」，这时才生成初始项。
    /// 文件存在但读不出来，返回空列表而不是 null——宁可显示空 Dock，
    /// 也不能把用户自己加的一排图标悄悄换回默认的那几个。
    /// </summary>
    public static List<DockItemConfig>? Load()
    {
        if (!File.Exists(FilePath) && !File.Exists(BackupPath)) return null;

        var fromMain = TryRead(FilePath);
        if (fromMain is not null) return fromMain;

        // 主文件坏了，退回上一次成功写入前的备份
        var fromBackup = TryRead(BackupPath);
        if (fromBackup is not null)
        {
            System.Diagnostics.Debug.WriteLine("[AquaDesk] dock.json 损坏，已从 .bak 恢复");
            // 顺手把主文件修回来，不然每次启动都要走一遍恢复
            Save(fromBackup);
            return fromBackup;
        }

        // 两个都读不出来：把坏文件留档，返回空列表（不重新初始化）
        try
        {
            if (File.Exists(FilePath))
            {
                var bad = FilePath + ".bad";
                NetFxShims.Move(FilePath, bad, overwrite: true);
                System.Diagnostics.Debug.WriteLine($"[AquaDesk] dock.json 无法解析，已改名到 {bad}");
            }
        }
        catch
        {
            // 忽略
        }

        return new List<DockItemConfig>();
    }

    public static void Save(IEnumerable<DockItemConfig> items)
    {
        var json = JsonSerializer.Serialize(items.ToList(), JsonOptions);
        SettingsStore.WriteAtomic(FilePath, json);
    }

    private static List<DockItemConfig>? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize<List<DockItemConfig>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 读取 {path} 失败：{ex.Message}");
            return null;
        }
    }
}
