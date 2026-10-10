namespace DireWolfGui.Core.Integrations;

/// <summary>How an application talks to Dire Wolf.</summary>
public enum TncProtocol { None, Agwpe, KissTcp, SerialKiss }

/// <summary>A configured external application (saved in the GUI settings).</summary>
public sealed class ExternalAppProfile
{
    public string Name { get; set; } = "";
    /// <summary>Id of the <see cref="ExternalAppTemplate"/> it was created from (null = custom).</summary>
    public string? TemplateId { get; set; }
    public string? ExePath { get; set; }
    /// <summary>Command line arguments, one entry per argument (passed with ProcessStartInfo.ArgumentList).</summary>
    public List<string> Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public TncProtocol Protocol { get; set; } = TncProtocol.Agwpe;
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>TCP port, or for serial KISS the baud rate.</summary>
    public int Port { get; set; } = 8000;
    /// <summary>Serial KISS: the port name (e.g. COM5) the application opens.</summary>
    public string? SerialPort { get; set; }
    public string? Notes { get; set; }
    /// <summary>Where the application keeps its own configuration (shown to the operator).</summary>
    public string? ConfigLocation { get; set; }

    public string ConnectionSummary => Protocol switch
    {
        TncProtocol.Agwpe => $"AGWPE (AGW TCP) at {Host}:{Port}",
        TncProtocol.KissTcp => $"KISS over TCP at {Host}:{Port}",
        TncProtocol.SerialKiss => $"Serial KISS on {SerialPort ?? "?"}",
        _ => "No TNC connection",
    };
}
