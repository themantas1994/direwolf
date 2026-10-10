using System.Windows;
using DireWolfGui.Core.Config;

namespace DireWolfGui.Views;

/// <summary>One line of the unified diff shown in <see cref="ConfigDiffDialog"/>.</summary>
public sealed record ConfigDiffRow(string Text, string Kind, string Spoken);

/// <summary>Diff preview before a configuration file is written (or a read-only comparison).</summary>
public partial class ConfigDiffDialog : Window
{
    public ConfigDiffDialog()
    {
        InitializeComponent();
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>
    /// Show a diff.  <paramref name="acceptText"/> null = read-only (Close button only).
    /// Returns true when the user pressed the accept button.  Texts must already be redacted.
    /// </summary>
    public static bool Show(string title, string heading, IReadOnlyList<DiffLine> diff, IEnumerable<string>? notes, string? acceptText,
        string oldName = "current file", string newName = "after saving")
    {
        var rows = new List<ConfigDiffRow>();
        string unified = TextDiff.ToUnified(diff, 3, oldName, newName);
        foreach (var line in TextDiff.SplitLines(unified))
        {
            string kind = line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("@@", StringComparison.Ordinal) ? "Info"
                : line.StartsWith('+') ? "Success"
                : line.StartsWith('-') ? "Error"
                : "Foreground";
            string body = kind != "Info" && line.Length > 0 ? line[1..] : line;
            string spoken = kind switch { "Success" => "Added: ", "Error" => "Removed: ", _ => "" } + body;
            rows.Add(new ConfigDiffRow(line, kind, spoken));
        }
        int added = diff.Count(d => d.Kind == DiffKind.Added), removed = diff.Count(d => d.Kind == DiffKind.Removed);
        var dlg = new ConfigDiffDialog { Title = title };
        dlg.HeadingText.Text = heading;
        dlg.SummaryText.Text = rows.Count == 0
            ? "No differences."
            : $"{added} line(s) added, {removed} line(s) removed (green: added, red: removed; up to 3 unchanged lines around each change).";
        dlg.DiffList.ItemsSource = rows;
        dlg.NotesList.ItemsSource = notes?.ToList() ?? [];
        if (acceptText == null)
        {
            dlg.AcceptButton.Visibility = Visibility.Collapsed;
            dlg.CancelButton.Content = "Close";
        }
        else dlg.AcceptButton.Content = acceptText;
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, dlg)) dlg.Owner = owner;
        return dlg.ShowDialog() == true;
    }
}
