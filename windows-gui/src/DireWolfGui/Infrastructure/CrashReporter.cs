using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DireWolfGui.Infrastructure;

/// <summary>
/// Last-resort error handling: writes a report (without configuration contents or
/// credentials) to %LOCALAPPDATA%\DireWolfStation\crash and tells the user where it is.
/// UI-thread errors are survivable and keep the application running.
/// </summary>
public static class CrashReporter
{
    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception, "fatal");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception, "background task");
            e.SetObserved();
        };
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = Write(e.Exception, "user interface");
        e.Handled = true;
        Dialogs.Error("Something went wrong in Dire Wolf Station. The application will keep running; Dire Wolf itself is not affected.",
            e.Exception.Message + (path is null ? "" : $"\n\nA report was saved to:\n{path}"));
    }

    public static string? Write(Exception? ex, string where)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CrashReports);
            var path = Path.Combine(AppPaths.CrashReports, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Dire Wolf Station {typeof(CrashReporter).Assembly.GetName().Version}");
            sb.AppendLine($"Time: {DateTimeOffset.Now:O}");
            sb.AppendLine($"Where: {where}");
            sb.AppendLine($"OS: {Environment.OSVersion}  .NET {Environment.Version}  64-bit process: {Environment.Is64BitProcess}");
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "(no exception information)");
            File.WriteAllText(path, sb.ToString());
            // Keep the folder small.
            foreach (var old in new DirectoryInfo(AppPaths.CrashReports).GetFiles("crash-*.txt")
                         .OrderByDescending(f => f.CreationTimeUtc).Skip(20))
                old.Delete();
            return path;
        }
        catch
        {
            return null;
        }
    }
}
