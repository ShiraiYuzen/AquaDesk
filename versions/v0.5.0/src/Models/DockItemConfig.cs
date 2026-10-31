using System.Text.Json.Serialization;

namespace AquaDesk.Models;

/// <summary>
/// 持久化用的 Dock 项。只存路径，图标每次启动重新取，避免缓存过期。
///
/// 普通应用就是 { Name, Target }；文件夹是 { Name, IsFolder: true, Items: [...] }，
/// 两个新字段都标了 WhenWritingDefault/WhenWritingNull，所以应用项写出来的 JSON 跟以前一模一样。
/// </summary>
public sealed class DockItemConfig
{
    public string Name { get; set; } = "";

    /// <summary>文件 / 快捷方式的完整路径；文件夹为空。</summary>
    public string Target { get; set; } = "";

    /// <summary>这一项是文件夹（里面装文件和程序）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsFolder { get; set; }

    /// <summary>
    /// 系统图标：Brand / Windows / Launchpad / Computer。
    /// 它们也排在 dock.json 里，所以能像普通图标一样拖动排序；
    /// 显隐由设置页的开关决定，关掉时这一项会被移出配置。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; set; }

    /// <summary>文件夹里的内容。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DockItemConfig>? Items { get; set; }
}
