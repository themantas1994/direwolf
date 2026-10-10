namespace DireWolfGui.Infrastructure;

/// <summary>Where the application keeps its own files (never inside the Dire Wolf folder).</summary>
public static class AppPaths
{
    public static string Roaming { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DireWolfStation");

    public static string Local { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DireWolfStation");

    public static string CrashReports => Path.Combine(Local, "crash");
    public static string ConsoleLogs => Path.Combine(Local, "console-logs");
    public static string PacketLogs => Path.Combine(Local, "logs");
    public static string Backups => Path.Combine(Roaming, "backups");
    public static string Profiles => Path.Combine(Roaming, "profiles");
    public static string Tiles => Path.Combine(Local, "tiles");
}
