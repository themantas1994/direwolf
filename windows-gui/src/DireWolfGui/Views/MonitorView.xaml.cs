using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DireWolfGui.ViewModels;

namespace DireWolfGui.Views;

public partial class MonitorView : UserControl
{
    private MonitorViewModel? _vm;
    private bool _scrollQueued;

    public MonitorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as MonitorViewModel);
        PacketGrid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopy, OnCanCopy));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        Attach(e.NewValue as MonitorViewModel);

    private void Attach(MonitorViewModel? vm)
    {
        if (_vm != null) _vm.ScrollToEndRequested -= OnScrollToEnd;
        _vm = vm;
        if (_vm != null) _vm.ScrollToEndRequested += OnScrollToEnd;
    }

    private void OnScrollToEnd(object? sender, EventArgs e)
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        // After layout, once per batch.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollQueued = false;
            if (PacketGrid.Items.Count > 0) PacketGrid.ScrollIntoView(PacketGrid.Items[PacketGrid.Items.Count - 1]);
        });
    }

    private void OnCanCopy(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = PacketGrid.SelectedItems.Count > 0;
        e.Handled = true;
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        _vm?.CopyRowsCommand.Execute(PacketGrid.SelectedItems);
        e.Handled = true;
    }
}
