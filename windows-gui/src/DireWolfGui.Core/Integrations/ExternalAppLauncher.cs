using System.ComponentModel;
using System.Diagnostics;

namespace DireWolfGui.Core.Integrations;

public sealed record LaunchResult(bool Success, int? ProcessId, string? Error)
{
    public static LaunchResult Fail(string error) => new(false, null, error);
}

/// <summary>Starts external applications and opens folders.  Never throws for expected failures:
/// problems are returned as <see cref="LaunchResult.Error"/>.</summary>
public static class ExternalAppLauncher
{
    public static LaunchResult Launch(ExternalAppProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ExePath)) return LaunchResult.Fail($"No program is set for \"{profile.Name}\".");
        if (!File.Exists(profile.ExePath)) return LaunchResult.Fail($"Program not found: {profile.ExePath}");
        if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory) && !Directory.Exists(profile.WorkingDirectory))
            return LaunchResult.Fail($"Working folder not found: {profile.WorkingDirectory}");
        var psi = new ProcessStartInfo(profile.ExePath)
        {
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? Path.GetDirectoryName(Path.GetFullPath(profile.ExePath))! : profile.WorkingDirectory,
        };
        foreach (var a in profile.Arguments ?? []) psi.ArgumentList.Add(a);
        return Start(psi);
    }

    /// <summary>Open a folder in the file manager (Explorer on Windows, xdg-open / open elsewhere).</summary>
    public static LaunchResult OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return LaunchResult.Fail($"Folder not found: {path}");
        string full = Path.GetFullPath(path);
        string tool = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false };
        psi.ArgumentList.Add(full);
        return Start(psi);
    }

    private static LaunchResult Start(ProcessStartInfo psi)
    {
        try
        {
            using var p = Process.Start(psi);
            return p == null ? LaunchResult.Fail($"\"{psi.FileName}\" did not start.") : new LaunchResult(true, p.Id, null);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return LaunchResult.Fail($"Could not start \"{psi.FileName}\": {e.Message}");
        }
    }
}
