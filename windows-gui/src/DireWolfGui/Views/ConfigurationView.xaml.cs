using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DireWolfGui.ViewModels;

namespace DireWolfGui.Views;

public partial class ConfigurationView : UserControl
{
    private ConfigurationViewModel? _vm;

    public ConfigurationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.GoToLineRequested -= GoToLine;
            _vm = DataContext as ConfigurationViewModel;
            if (_vm != null) _vm.GoToLineRequested += GoToLine;
        };
        Unloaded += (_, _) => { if (_vm != null) _vm.GoToLineRequested -= GoToLine; };
        Loaded += (_, _) => { if (_vm != null) { _vm.GoToLineRequested -= GoToLine; _vm.GoToLineRequested += GoToLine; } };
    }

    /// <summary>Select a 1-based line in the raw editor (computed from the text, so it works before layout).</summary>
    private void GoToLine(object? sender, int line)
    {
        string text = RawEditor.Text ?? "";
        int start = 0, current = 1;
        while (current < line && start < text.Length)
        {
            int nl = text.IndexOfAny(['\r', '\n'], start);
            if (nl < 0) { start = text.Length; break; }
            start = nl + (text[nl] == '\r' && nl + 1 < text.Length && text[nl + 1] == '\n' ? 2 : 1);
            current++;
        }
        int end = text.IndexOfAny(['\r', '\n'], Math.Min(start, text.Length));
        if (end < 0) end = text.Length;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            int len = (RawEditor.Text ?? "").Length;
            RawEditor.Focus();
            RawEditor.Select(Math.Min(start, len), Math.Max(0, Math.Min(end, len) - Math.Min(start, len)));
            RawEditor.ScrollToLine(Math.Max(0, line - 1));
        });
    }

    private void RawEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        int index = RawEditor.CaretIndex;
        string text = RawEditor.Text ?? "";
        int line = 1, col = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n') { line++; col = 1; }
            else if (text[i] != '\r') col++;
        }
        CaretText.Text = $"Line {line}, column {col}";
    }
}
