using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DireWolfGui.ViewModels;

namespace DireWolfGui.Infrastructure;

/// <summary>
/// "DireWolfStation.exe --smoke-test result.txt": opens every page in the light and dark
/// themes, lets each render, records binding and XAML errors, writes a report and exits
/// with 0 (clean) or 1 (errors).  Used by Windows CI, where nobody can look at the screen.
/// It never starts Dire Wolf and never transmits.
/// </summary>
public static class SmokeTest
{
    private static readonly List<string> Errors = [];

    private sealed class Listener : TraceListener
    {
        private readonly StringBuilder _line = new();
        public override void Write(string? message) => _line.Append(message);
        public override void WriteLine(string? message)
        {
            _line.Append(message);
            lock (Errors) Errors.Add(_line.ToString());
            _line.Clear();
        }
    }

    public static string? ReportPath(string[] args)
    {
        var i = Array.IndexOf(args, "--smoke-test");
        return i < 0 ? null : i + 1 < args.Length ? args[i + 1] : "smoke-test.txt";
    }

    public static void InstallListeners()
    {
        PresentationTraceSources.Refresh();
        foreach (var source in new[] { PresentationTraceSources.DataBindingSource, PresentationTraceSources.ResourceDictionarySource, PresentationTraceSources.MarkupSource })
        {
            source.Listeners.Add(new Listener());
            source.Switch.Level = SourceLevels.Error;
        }
    }

    public static void RecordError(string text)
    {
        lock (Errors) Errors.Add(text);
    }

    public static async Task RunAsync(MainWindow window, MainViewModel vm, string reportPath)
    {
        var visited = new List<string>();
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                ThemeManager.Apply(theme);
                foreach (var page in vm.Pages.ToList())
                {
                    vm.SelectedPage = page;
                    for (var i = 0; i < 3; i++)
                    {
                        page.Tick();
                        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                        await Task.Delay(150);
                    }
                    visited.Add($"{theme}: {page.Title}");
                }
            }
        }
        catch (Exception ex)
        {
            RecordError("Exception: " + ex);
        }

        string[] errors;
        lock (Errors) errors = Errors.Distinct().ToArray();
        var report = new StringBuilder();
        report.AppendLine($"Dire Wolf Station smoke test {DateTimeOffset.Now:O}");
        report.AppendLine("Pages shown:");
        foreach (var v in visited) report.AppendLine("  " + v);
        report.AppendLine(errors.Length == 0 ? "No binding, resource or XAML errors." : $"{errors.Length} error(s):");
        foreach (var e in errors) report.AppendLine("  " + e);
        File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown(errors.Length == 0 ? 0 : 1);
    }
}
