using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DireWolfGui.ViewModels;

namespace DireWolfGui.Views;

public partial class TerminalView : UserControl
{
    private TerminalViewModel? _vm;
    private bool _scrollQueued;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as TerminalViewModel);
        Loaded += (_, _) => { Attach(DataContext as TerminalViewModel); ScrollToEnd(); };
        Unloaded += (_, _) => Attach(null);
        InputBox.PreviewKeyDown += OnInputKeyDown;
        Output.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopy, (_, e) =>
        {
            e.CanExecute = Output.SelectedItems.Count > 0;
            e.Handled = true;
        }));
    }

    private void Attach(TerminalViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm != null) _vm.ScrollToEndRequested -= OnScrollToEnd;
        _vm = vm;
        if (_vm != null) _vm.ScrollToEndRequested += OnScrollToEnd;
    }

    private void OnScrollToEnd(object? sender, EventArgs e) => ScrollToEnd();

    private void ScrollToEnd()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollQueued = false;
            if (Output.Items.Count > 0) Output.ScrollIntoView(Output.Items[Output.Items.Count - 1]);
        });
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Up) { _vm.HistoryPrevious(); InputBox.CaretIndex = InputBox.Text.Length; e.Handled = true; }
        else if (e.Key == Key.Down) { _vm.HistoryNext(); InputBox.CaretIndex = InputBox.Text.Length; e.Handled = true; }
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        var lines = Output.SelectedItems.OfType<TerminalLine>().OrderBy(l => Output.Items.IndexOf(l)).Select(l => l.ToString());
        ClipboardHelper.TrySetText(string.Join(Environment.NewLine, lines));
        e.Handled = true;
    }
}
