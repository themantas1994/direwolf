using System.Windows;
using DireWolfGui.Infrastructure;
using DireWolfGui.ViewModels;

namespace DireWolfGui;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        CrashReporter.Install(this);
        base.OnStartup(e);

        // One instance per Windows user: two copies would fight over the same Dire Wolf.
        _single = new Mutex(true, @"Local\DireWolfStation-single-instance", out var first);
        if (!first)
        {
            MessageBox.Show("Dire Wolf Station is already running.", "Dire Wolf Station", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        MainViewModel vm;
        try
        {
            vm = new MainViewModel();
        }
        catch (Exception ex)
        {
            var path = CrashReporter.Write(ex, "startup");
            MessageBox.Show("Dire Wolf Station could not start: " + ex.Message + (path is null ? "" : "\n\nReport: " + path),
                "Dire Wolf Station", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        ThemeManager.Initialize(vm.Settings.Theme.ToString());
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        if (!vm.Settings.FirstRunCompleted)
            vm.NavigateCommand.Execute(vm.Pages.IndexOf(vm.Pages.FirstOrDefault(p => p.GetType().Name == "SetupWizardViewModel") ?? vm.Pages[0]).ToString());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
