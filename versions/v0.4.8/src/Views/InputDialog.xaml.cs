using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AquaDesk.Views;

/// <summary>
/// 一个极简的输入框，目前只有「重命名文件夹」用。
/// WPF 没有内置 InputBox，自己画一个比调 Microsoft.VisualBasic 干净。
/// </summary>
public partial class InputDialog : Window
{
    public InputDialog(string title, string initial)
    {
        InitializeComponent();

        TitleText.Text = title;
        ValueBox.Text = initial;

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text.Trim();

    /// <summary>弹出输入框；取消返回 null。</summary>
    public static string? Ask(string title, string initial)
    {
        var dialog = new InputDialog(title, initial);
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void OnValueChanged(object sender, TextChangedEventArgs e) =>
        OkButton.IsEnabled = ValueBox.Text.Trim().Length > 0;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 松手太快，忽略
        }
    }
}
