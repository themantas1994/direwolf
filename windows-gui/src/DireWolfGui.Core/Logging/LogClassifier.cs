using System.Text.RegularExpressions;

namespace DireWolfGui.Core.Logging;

/// <summary>Assigns a severity and category to a Dire Wolf console line (heuristic, text based).</summary>
public static partial class LogClassifier
{
    [GeneratedRegex(@"^\[(\d+[HL]|\d+>is|\d+>nt|rx>ig|ig>tx)[\] ]")]
    private static partial Regex TransmitLabel();

    [GeneratedRegex(@"^\[(\d+(\.\d+){0,2}|\d+\.(is|dtmf|AIS)|ig)[\] ]")]
    private static partial Regex ReceiveLabel();

    [GeneratedRegex(@"^Line \d+:")]
    private static partial Regex ConfigLine();

    private static readonly string[] ErrorWords =
    {
        "ERROR", "Error ", "error:", "Could not", "could not", "Can't", "can't open", "Cannot", "cannot",
        "Failed", "failed", "FATAL", "Fatal", "No such file", "Permission denied", "Address already in use",
    };

    private static readonly string[] WarningWords =
    {
        "Warning", "WARNING", "too high", "too low", "Why are you running this as root", "Invalid", "invalid",
        "not gated", "gave up", "not sent", "can't keep up", "Duplicate", "obsolete", "is deprecated",
        "not configured", "sending it again", "Lost connection", "disconnected", "Closing connection",
    };

    public static (LogSeverity Severity, string Category) Classify(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (LogSeverity.Debug, "direwolf");
        string category = Categorize(line);

        if (TransmitLabel().IsMatch(line)) return (LogSeverity.Transmit, category);
        if (line.StartsWith("[WAPR gate", StringComparison.Ordinal)) return (LogSeverity.Warning, "wapr");
        if (ReceiveLabel().IsMatch(line)) return (LogSeverity.Packet, category);
        if (line.Contains("audio level = ", StringComparison.Ordinal)) return (LogSeverity.Packet, "audio");

        foreach (var w in ErrorWords)
            if (line.Contains(w, StringComparison.Ordinal)) return (LogSeverity.Error, category);
        if (ConfigLine().IsMatch(line) || line.StartsWith("Config file", StringComparison.Ordinal))
            return (line.Contains("Warning", StringComparison.OrdinalIgnoreCase) ? LogSeverity.Warning : LogSeverity.Error, "config");
        foreach (var w in WarningWords)
            if (line.Contains(w, StringComparison.Ordinal)) return (LogSeverity.Warning, category);
        return (LogSeverity.Info, category);
    }

    private static string Categorize(string line)
    {
        if (line.StartsWith("[ig", StringComparison.Ordinal) || line.StartsWith("[rx>ig", StringComparison.Ordinal)
            || line.Contains(">is]", StringComparison.Ordinal) || line.Contains(".is]", StringComparison.Ordinal)
            || line.Contains("IGate", StringComparison.OrdinalIgnoreCase) || line.Contains("APRS-IS", StringComparison.Ordinal))
            return "igate";
        if (line.Contains("WAPR", StringComparison.Ordinal)) return "wapr";
        if (line.Contains("AGW", StringComparison.Ordinal)) return "agw";
        if (line.Contains("KISS", StringComparison.Ordinal)) return "kiss";
        if (line.StartsWith("ADEVICE", StringComparison.Ordinal) || line.Contains("audio", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Audio", StringComparison.Ordinal))
            return "audio";
        if (line.Contains("PTT", StringComparison.Ordinal)) return "ptt";
        if (ConfigLine().IsMatch(line) || line.Contains("config", StringComparison.OrdinalIgnoreCase)) return "config";
        return "direwolf";
    }
}
