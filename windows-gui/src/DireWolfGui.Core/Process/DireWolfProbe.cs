using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SysProcess = System.Diagnostics.Process;

namespace DireWolfGui.Core.Process;

/// <summary>Version and build features reported by a direwolf executable.</summary>
public sealed record DireWolfVersionInfo(string ExecutablePath, string VersionText, int? Major, int? Minor, string? Patch, bool IsDevelopment,
    IReadOnlyList<string> Features, IReadOnlyList<string> Banner)
{
    public string DisplayVersion => Major.HasValue ? $"{Major}.{Minor}{(Patch != null ? "." + Patch : "")}{(IsDevelopment ? " (development)" : "")}" : VersionText;
}

/// <summary>A direwolf process found on the machine (possibly started outside this program).</summary>
public sealed record RunningDireWolfInstance(int ProcessId, string? ExecutablePath, DateTime? StartTime, bool ManagedByThisProgram);

public static partial class DireWolfProbe
{
    [GeneratedRegex(@"^Dire Wolf (?:(?<dev>DEVELOPMENT) )?(?:Release|version) (?<maj>\d+)\.(?<min>\d+)(?:\.(?<patch>\d+))?(?:\s*(?<rest>.*))?$")]
    private static partial Regex BannerRegex();

    public static string ExecutableName => OperatingSystem.IsWindows() ? "direwolf.exe" : "direwolf";

    /// <summary>Parses "Dire Wolf Release 1.8.2, November 2025" / "Dire Wolf version 1.7" / "Dire Wolf DEVELOPMENT version 1.8 E (date)".</summary>
    public static DireWolfVersionInfo? ParseBanner(string executablePath, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        var banner = all.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Dire Wolf ", StringComparison.Ordinal));
        if (banner == null) return null;
        var m = BannerRegex().Match(banner);
        var features = new List<string>();
        var feat = all.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Includes optional support for:", StringComparison.Ordinal));
        if (feat != null) features.AddRange(feat["Includes optional support for:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string text = banner["Dire Wolf ".Length..];
        if (!m.Success) return new DireWolfVersionInfo(executablePath, text, null, null, null, text.Contains("DEVELOPMENT", StringComparison.Ordinal), features, all);
        return new DireWolfVersionInfo(executablePath, text, int.Parse(m.Groups["maj"].Value), int.Parse(m.Groups["min"].Value),
            m.Groups["patch"].Success ? m.Groups["patch"].Value : null, m.Groups["dev"].Success, features, all);
    }

    /// <summary>Runs "direwolf -t 0 -u" (prints the banner and a UTF-8 test, opens no devices) and parses the banner.</summary>
    public static async Task<DireWolfVersionInfo?> ProbeAsync(string executablePath, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!File.Exists(executablePath)) return null;
        var psi = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-u");
        SysProcess? p;
        try { p = SysProcess.Start(psi); }
        catch (Win32Exception) { return null; }
        if (p == null) return null;
        using (p)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            try
            {
                var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
                var stderr = p.StandardError.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                string text = await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false);
                return ParseBanner(executablePath, text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(20));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }
        }
    }

    /// <summary>Places where direwolf is commonly installed, most likely first. Not all exist.</summary>
    public static IReadOnlyList<string> CandidateExecutablePaths(string? guiDirectory = null)
    {
        var list = new List<string>();
        guiDirectory ??= AppContext.BaseDirectory;
        list.Add(Path.Combine(guiDirectory, ExecutableName));
        list.Add(Path.Combine(guiDirectory, "direwolf", ExecutableName));
        if (OperatingSystem.IsWindows())
        {
            list.Add(@"C:\direwolf\direwolf.exe");
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (!string.IsNullOrEmpty(pf)) list.Add(Path.Combine(pf, "direwolf", "direwolf.exe"));
            list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "direwolf", "direwolf.exe"));
        }
        else
        {
            list.Add("/usr/local/bin/direwolf");
            list.Add("/usr/bin/direwolf");
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { list.Add(Path.Combine(dir.Trim('"'), ExecutableName)); } catch (ArgumentException) { }
        }
        return list.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
    }

    public static string? FindExecutable(string? guiDirectory = null) => CandidateExecutablePaths(guiDirectory).FirstOrDefault(File.Exists);

    /// <summary>direwolf processes currently running on this machine, including ones started outside this program.</summary>
    public static IReadOnlyList<RunningDireWolfInstance> FindRunningInstances(IEnumerable<int>? managedProcessIds = null)
    {
        var managed = new HashSet<int>(managedProcessIds ?? Array.Empty<int>());
        var result = new List<RunningDireWolfInstance>();
        foreach (var p in SysProcess.GetProcessesByName("direwolf"))
        {
            using (p)
            {
                string? path = null;
                DateTime? start = null;
                try { path = p.MainModule?.FileName; } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                try { start = p.StartTime; } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                result.Add(new RunningDireWolfInstance(p.Id, path, start, managed.Contains(p.Id)));
            }
        }
        return result;
    }
}
