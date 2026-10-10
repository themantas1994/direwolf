using System.Globalization;

namespace DireWolfGui.Core.Process;

public enum DireWolfState { Stopped, Starting, Running, Stopping, Failed }

/// <summary>How to start direwolf. The GUI never adds -x (calibration tones) on its own.</summary>
public sealed class DireWolfLaunchOptions
{
    public required string ExecutablePath { get; init; }
    public required string ConfigPath { get; init; }
    /// <summary>Working directory; default is the executable's directory.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>-a n: audio statistics interval in seconds (Dire Wolf warns below 10).</summary>
    public int? AudioStatsIntervalSeconds { get; init; }
    /// <summary>-l dir: daily CSV log files (YYYY-MM-DD.log, UTC date) in this directory.</summary>
    public string? LogDirectory { get; init; }
    /// <summary>-L file: single CSV log file. Mutually exclusive with <see cref="LogDirectory"/>.</summary>
    public string? LogFile { get; init; }
    /// <summary>Additional arguments passed as-is (one element per argument). "-x" is refused.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = Array.Empty<string>();

    /// <summary>
    /// TEST / DIAGNOSTIC ONLY: redirect direwolf's standard input so raw S16LE audio can be written with
    /// <see cref="DireWolfProcessController.WriteStandardInputAsync"/>. Adds "-" (read audio from stdin).
    /// The configuration must use "ADEVICE stdin ...". Never enabled by the GUI.
    /// </summary>
    public bool StandardInputAudio { get; init; }

    public string EffectiveWorkingDirectory =>
        WorkingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(ExecutablePath)) ?? Environment.CurrentDirectory;

    /// <summary>Builds the argument list: always "-t 0 -c &lt;conf&gt;" first, then -a, -l/-L, extras.</summary>
    public static IReadOnlyList<string> BuildArgumentList(DireWolfLaunchOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (string.IsNullOrWhiteSpace(o.ConfigPath)) throw new ArgumentException("A configuration file is required.", nameof(o));
        if (o.LogDirectory != null && o.LogFile != null) throw new ArgumentException("Use either a log directory (-l) or a log file (-L), not both.", nameof(o));
        var args = new List<string> { "-t", "0", "-c", o.ConfigPath };
        if (o.AudioStatsIntervalSeconds is int a)
        {
            if (a < 1) throw new ArgumentOutOfRangeException(nameof(o), "Audio statistics interval must be at least 1 second.");
            args.Add("-a");
            args.Add(a.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(o.LogDirectory)) { args.Add("-l"); args.Add(o.LogDirectory); }
        if (!string.IsNullOrWhiteSpace(o.LogFile)) { args.Add("-L"); args.Add(o.LogFile); }
        foreach (var e in o.ExtraArguments)
        {
            if (e.Trim() is "-x" || (e.StartsWith("-x", StringComparison.Ordinal) && e.Length <= 3))
                throw new ArgumentException("The calibration option -x transmits continuously; it is never passed automatically.", nameof(o));
            args.Add(e);
        }
        if (o.StandardInputAudio) args.Add("-");
        return args;
    }
}
