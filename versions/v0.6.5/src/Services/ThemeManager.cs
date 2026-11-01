using System.Windows;
using AquaDesk.Models;

namespace AquaDesk.Services;

/// <summary>
/// 换肤：把设置页 / 右键菜单的配色字典换成当前外观那一份。
///
/// 做法是「原位替换 MergedDictionaries 里的 UiTheme.*.xaml」——
/// 控件模板里全是 DynamicResource，替换字典后所有已打开的窗口会立刻重画，
/// 不需要重建窗口。UiControls.xaml（模板）只加载一次，不参与替换。
/// </summary>
public static class ThemeManager
{
    private const string ThemePrefix = "UiTheme.";

    private static DockStyle? _current;

    public static void Apply(DockStyle style)
    {
        if (_current == style) return;
        _current = style;

        var app = Application.Current;
        if (app is null) return;

        var dictionaries = app.Resources.MergedDictionaries;

        var name = style switch
        {
            DockStyle.LiquidClear => "Clear",
            DockStyle.Classic => "Classic",
            _ => "Liquid"
        };

        var target = new ResourceDictionary
        {
            Source = new Uri($"Themes/UiTheme.{name}.xaml", UriKind.Relative)
        };

        var existing = dictionaries.FirstOrDefault(
            d => d.Source?.OriginalString.Contains(ThemePrefix, StringComparison.Ordinal) == true);

        if (existing is not null)
        {
            // 原位替换：保持合并顺序，DynamicResource 会跟着刷新
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = target;
        }
        else
        {
            dictionaries.Add(target);
        }

        App.Log($"换肤：{name}", null);
    }
}
