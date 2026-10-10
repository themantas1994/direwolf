using System.Windows.Controls;
using System.Windows.Threading;
using DireWolfGui.ViewModels;

namespace DireWolfGui.Views;

public partial class MessagesView : UserControl
{
    private MessagesViewModel? _vm;

    public MessagesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MessagesViewModel);
        Loaded += (_, _) =>
        {
            Attach(DataContext as MessagesViewModel);
            ScrollToEnd();
            if (_vm?.TakeFocusRequest() == true) OnFocusCompose(this, EventArgs.Empty);
        };
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(MessagesViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm != null)
        {
            _vm.ScrollToEndRequested -= OnScrollToEnd;
            _vm.FocusComposeRequested -= OnFocusCompose;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.ScrollToEndRequested += OnScrollToEnd;
            _vm.FocusComposeRequested += OnFocusCompose;
        }
    }

    private void OnScrollToEnd(object? sender, EventArgs e) => ScrollToEnd();

    private void ScrollToEnd() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (MessageList.Items.Count > 0) MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1]);
        });

    private void OnFocusCompose(object? sender, EventArgs e)
    {
        _vm?.TakeFocusRequest();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ComposeBox.Focus());
    }
}
