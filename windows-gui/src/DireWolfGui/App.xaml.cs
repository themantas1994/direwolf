using System.Windows;
using DireWolfGui.Infrastructure;

namespace DireWolfGui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        CrashReporter.Install(this);
        base.OnStartup(e);
        ThemeManager.Initialize("System");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
