using System.Windows;

namespace DireWolfGui.Views;

/// <summary>Asks for one line of text (profile names and the like).</summary>
public partial class ConfigInputDialog : Window
{
    public ConfigInputDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Returns the trimmed text, or null when cancelled or empty.</summary>
    public static string? Ask(string title, string prompt, string? initial = null)
    {
        var dlg = new ConfigInputDialog { Title = title };
        dlg.PromptText.Text = prompt;
        dlg.InputBox.Text = initial ?? "";
        dlg.InputBox.SelectAll();
        if (Application.Current?.MainWindow is { IsLoaded: true } owner) dlg.Owner = owner;
        if (dlg.ShowDialog() != true) return null;
        var text = dlg.InputBox.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}
