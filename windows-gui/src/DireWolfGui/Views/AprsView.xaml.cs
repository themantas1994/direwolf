using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DireWolfGui.Controls;
using DireWolfGui.ViewModels;

namespace DireWolfGui.Views;

public partial class AprsView : UserControl
{
    private AprsViewModel? _vm;
    private IReadOnlyList<MapMarker>? _pendingFit;

    public AprsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as AprsViewModel);
        Loaded += (_, _) => Attach(DataContext as AprsViewModel);
        Unloaded += (_, _) => Attach(null);
        Map.SizeChanged += (_, _) =>
        {
            if (_pendingFit != null && Map.ActualWidth >= 10) { var m = _pendingFit; _pendingFit = null; Map.FitTo(m); }
        };
        StationGrid.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement { DataContext: StationItemViewModel } && _vm?.CenterSelectedCommand.CanExecute(null) == true)
                _vm.CenterSelectedCommand.Execute(null);
        };
        StationGrid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _vm?.CenterSelectedCommand.CanExecute(null) == true)
            {
                _vm.CenterSelectedCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void Attach(AprsViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;
        if (_vm != null)
        {
            _vm.FitRequested -= OnFit;
            _vm.CenterRequested -= OnCenter;
            _vm.SelectionScrollRequested -= OnScrollToSelection;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.FitRequested += OnFit;
            _vm.CenterRequested += OnCenter;
            _vm.SelectionScrollRequested += OnScrollToSelection;
        }
    }

    private void OnFit(object? sender, IReadOnlyList<MapMarker> markers) => Fit(markers);

    private void Fit(IReadOnlyList<MapMarker> markers)
    {
        if (markers.Count == 0) return;
        if (Map.ActualWidth < 10) { _pendingFit = markers; return; }
        Map.FitTo(markers);
    }

    private void OnCenter(object? sender, MapMarker m)
    {
        Map.CenterLatitude = m.Latitude;
        Map.CenterLongitude = m.Longitude;
        if (Map.Zoom < 10) Map.Zoom = 10;
    }

    private void OnScrollToSelection(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (StationGrid.SelectedItem != null) StationGrid.ScrollIntoView(StationGrid.SelectedItem);
        });
}
