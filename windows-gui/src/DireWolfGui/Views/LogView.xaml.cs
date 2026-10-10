using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DireWolfGui.Core.Logging;
using DireWolfGui.ViewModels;

namespace DireWolfGui.Views;

public partial class LogView : UserControl
{
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is LogViewModel o) o.LinesAppended -= OnAppended;
            if (e.NewValue is LogViewModel n) n.LinesAppended += OnAppended;
        };
        InputBindings.Add(new KeyBinding(new Infrastructure.RelayCommand(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
    }

    private void OnAppended(object? sender, EventArgs e)
    {
        if (DataContext is LogViewModel { AutoScroll: true } && List.Items.Count > 0)
            List.ScrollIntoView(List.Items[^1]);
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var item in List.SelectedItems.OfType<LogEntry>().OrderBy(x => x.Sequence))
            sb.Append(item.Time.ToLocalTime().ToString("HH:mm:ss.fff")).Append('\t').AppendLine(item.Text);
        if (sb.Length > 0) Clipboard.SetText(sb.ToString());
    }
}
