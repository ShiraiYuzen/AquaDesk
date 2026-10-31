using System.IO;
using System.Text.Json;
using AquaDesk.Models;

namespace AquaDesk.Services;

/// <summary>配置落盘位置：%AppData%\AquaDesk\settings.json</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AquaDesk");

    private static string FilePath => Path.Combine(RootDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
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

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AquaDesk] 写入 {path} 失败：{ex.Message}");
        }
    }
}
