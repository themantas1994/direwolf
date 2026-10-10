namespace DireWolfGui.Core.Logging;

public enum LogSeverity { Debug, Info, Packet, Transmit, Warning, Error }

/// <summary>One line of Dire Wolf (or GUI) output, already redacted.</summary>
public sealed class LogEntry : ISequenced
{
    public LogEntry(DateTimeOffset time, LogSeverity severity, string text, string category = "direwolf")
    {
        Time = time;
        Severity = severity;
        Text = text;
        Category = category;
    }

    public long Sequence { get; set; }
    public DateTimeOffset Time { get; }
    public LogSeverity Severity { get; }
    public string Text { get; }
    /// <summary>Where the line came from or what it is about, e.g. "direwolf", "stderr", "gui", "agw", "igate", "wapr", "audio".</summary>
    public string Category { get; }

    public override string ToString() => $"{Time:HH:mm:ss} [{Severity}] {Text}";
}
