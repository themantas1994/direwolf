namespace DireWolfGui.Core.Config;

public enum DiagnosticSeverity { Info, Warning, Error }

public enum DiagnosticSource { Gui, DireWolf }

/// <summary>A problem or note about a configuration.  <see cref="Line"/> is 1-based, null when not tied to a line.</summary>
public sealed record ConfigDiagnostic(
    DiagnosticSeverity Severity,
    int? Line,
    string Code,
    string Message,
    DiagnosticSource Source = DiagnosticSource.Gui)
{
    public override string ToString() => $"{Severity}{(Line is int l ? $" line {l}" : "")}: {Message}";
}
