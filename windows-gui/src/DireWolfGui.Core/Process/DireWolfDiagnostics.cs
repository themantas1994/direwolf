using DireWolfGui.Core.Logging;

namespace DireWolfGui.Core.Process;

/// <summary>Explanation of why direwolf stopped, derived from its last output lines.</summary>
public sealed record DireWolfDiagnosis(string Summary, IReadOnlyList<string> ErrorLines);

public static class DireWolfDiagnostics
{
    private static readonly (string Pattern, string Explanation)[] Known =
    {
        ("Could not open configuration file", "The configuration file could not be opened. Check the configuration path."),
        ("Could not open audio device", "The audio device could not be opened. Check ADEVICE in the configuration and that no other program is using the sound card."),
        ("Address already in use", "A TCP port (AGWPORT or KISSPORT) is already in use, probably by another Dire Wolf or AGW/KISS program."),
        ("Could not bind", "A TCP port (AGWPORT or KISSPORT) could not be opened, probably because it is already in use."),
        ("End of file on stdin", "Audio input from standard input ended."),
        ("Pseudo terminal", "The pseudo terminal (KISS serial emulation) could not be set up."),
        ("PTT", "The PTT (push to talk) control could not be set up. Check the PTT line in the configuration."),
    };

    /// <summary>Builds a short, user facing explanation. <paramref name="lastLines"/> should be the last ~100 output lines.</summary>
    public static DireWolfDiagnosis Diagnose(IEnumerable<string> lastLines, int? exitCode)
    {
        var lines = lastLines.Select(SecretRedactor.Redact).ToList();
        var errors = lines.Where(l => LogClassifier.Classify(l).Severity == LogSeverity.Error
                                      || l.StartsWith("Line ", StringComparison.Ordinal)).TakeLast(10).ToList();

        foreach (var (pattern, explanation) in Known)
        {
            var hit = lines.LastOrDefault(l => l.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                                               && (pattern != "PTT" || errors.Contains(l)));
            if (hit != null) return new DireWolfDiagnosis($"{explanation} (Dire Wolf said: \"{hit.Trim()}\")", errors);
        }
        var config = errors.LastOrDefault(l => l.StartsWith("Line ", StringComparison.Ordinal));
        if (config != null) return new DireWolfDiagnosis("Configuration problem: " + config.Trim(), errors);
        if (errors.Count > 0) return new DireWolfDiagnosis("Dire Wolf reported: " + errors[^1].Trim(), errors);
        return new DireWolfDiagnosis(exitCode switch
        {
            null => "Dire Wolf stopped unexpectedly.",
            unchecked((int)0xC0000005) => "Dire Wolf crashed (access violation).",
            unchecked((int)0xC000013A) => "Dire Wolf was interrupted (Ctrl+C / console closed).",
            134 or 139 or -6 or -11 => $"Dire Wolf crashed (exit code {exitCode}).",
            _ => $"Dire Wolf exited unexpectedly with code {exitCode}.",
        }, errors);
    }
}
