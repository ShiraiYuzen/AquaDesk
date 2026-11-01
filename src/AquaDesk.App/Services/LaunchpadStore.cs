using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AquaDesk.Services;

/// <summary>启动台的布局：格子顺序、文件夹结构、被隐藏的应用。</summary>
public sealed class LaunchpadLayout
{
    public List<LaunchpadLayoutEntry> Entries { get; set; } = new();

    /// <summary>用户手动移除过的应用（不真删，只是不再出现在启动台）。</summary>
    public List<string> Hidden { get; set; } = new();
}

public sealed class LaunchpadLayoutEntry
{
    /// <summary>"app" 或 "folder"。</summary>
    public string Kind { get; set; } = "app";

    public string? Target { get; set; }

    public string? Name { get; set; }

    /// <summary>文件夹里各应用的路径。</summary>
    public List<string> Items { get; set; } = new();
}

/// <summary>launchpad.json 的读写。写盘是原子写，坏文件不会覆盖好备份。</summary>
public static class LaunchpadStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AquaDesk-net48");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "launchpad.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static LaunchpadLayout? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<LaunchpadLayout>(json, Options);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 读取 launchpad.json 失败：{ex.Message}");
            return null;
        }
    }

    public static void Save(LaunchpadLayout layout)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            var json = JsonSerializer.Serialize(layout, Options);
            var temp = FilePath + ".tmp";

            File.WriteAllText(temp, json);
            NetFxShims.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AquaDesk] 写入 launchpad.json 失败：{ex.Message}");
        }
    }
}
