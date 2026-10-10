using System.ComponentModel;
using System.Windows;
using DireWolfGui.Core.Settings;
using DireWolfGui.Infrastructure;
using DireWolfGui.ViewModels;

namespace DireWolfGui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _closingConfirmed;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        RestorePlacement(vm.Settings.Window);
        if (vm.Settings.PanelSizes.TryGetValue("nav", out var nav) && nav >= 56) vm.NavWidth = new GridLength(nav);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed) return;
        if (_vm.IsDireWolfRunning &&
            !Dialogs.Confirm("Dire Wolf is running. Closing Dire Wolf Station stops it cleanly (the transmitter is released).\n\nClose and stop Dire Wolf?",
                "Close Dire Wolf Station"))
        {
            e.Cancel = true;
            return;
        }
        // Finish the clean stop before the window really closes.
        e.Cancel = true;
        IsEnabled = false;
        SavePlacement();
        await _vm.DisposeAsync();
        _closingConfirmed = true;
        Close();
    }

    private void RestorePlacement(WindowPlacement? p)
    {
        if (p is null || p.Width < 400 || p.Height < 300) return;
        // Only restore a position that is still on a screen (monitors can change).
        var visible = p.Left >= SystemParameters.VirtualScreenLeft - 50 && p.Top >= SystemParameters.VirtualScreenTop - 50 &&
                      p.Left + 100 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
                      p.Top + 50 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (visible)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left;
            Top = p.Top;
        }
        Width = p.Width;
        Height = p.Height;
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _vm.Settings.Window = new WindowPlacement
        {
            Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        _vm.Settings.PanelSizes["nav"] = _vm.NavWidth.Value;
    }
}
