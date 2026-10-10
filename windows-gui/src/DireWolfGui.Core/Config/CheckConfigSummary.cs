namespace DireWolfGui.Core.Config;

public sealed record CheckAudioDevice(int Index, string Input, string Output, int SampleRate, int Channels);

public enum ChannelMedium { Radio, IGate, NetTnc }

public sealed record CheckChannel(
    int Number,
    ChannelMedium Medium,
    string MyCall,
    string? Modem = null,
    int Baud = 0,
    int Mark = 0,
    int Space = 0,
    string? Profiles = null,
    string? Ptt = null,
    int? Fx25Tx = null,
    int? Il2pTx = null,
    string? WaprProfile = null,
    double? WaprAirtimePercent = null)
{
    public bool IsWapr => string.Equals(Modem, "WAPR", StringComparison.OrdinalIgnoreCase);
}

public sealed record CheckKissPort(int Port, int Channel);
public sealed record CheckSerialKiss(string Device, int Speed);
public sealed record CheckChannelPair(int From, int To);
public sealed record CheckIGate(string Server, int Port, string Login, int TxChannel);
public sealed record CheckBeacon(string Type, string SendTo, int Channel, int Line);

/// <summary>WAPR gateway rule; <see cref="To"/> is -1 for APRS-IS.  <see cref="Types"/> is the WAPR_GT_* bit mask.</summary>
public sealed record CheckWaprGate(int From, int To, int Types)
{
    public bool ToInternet => To < 0;
}

/// <summary>Typed form of the "check-config: ..." lines printed by <c>direwolf --check-config</c>.</summary>
public sealed class CheckConfigSummary
{
    public string? Version { get; set; }
    public HashSet<string> Features { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CheckAudioDevice> AudioDevices { get; } = [];
    public List<CheckChannel> Channels { get; } = [];
    /// <summary>AGW port, null when disabled (AGWPORT 0).</summary>
    public int? AgwPort { get; set; }
    /// <summary>True when "check-config: tcpbind local" was printed (TCPBIND LOCAL): servers listen on 127.0.0.1 only.</summary>
    public bool BindLocalOnly { get; set; }
    /// <summary>True if the build reported a tcpbind line at all (older builds don't know TCPBIND).</summary>
    public bool TcpBindReported { get; set; }
    public List<CheckKissPort> KissPorts { get; } = [];
    public CheckSerialKiss? SerialKiss { get; set; }
    public List<CheckChannelPair> Digipeat { get; } = [];
    public List<CheckChannelPair> Regen { get; } = [];
    public List<CheckChannelPair> CDigipeat { get; } = [];
    public CheckIGate? IGate { get; set; }
    public List<CheckBeacon> Beacons { get; } = [];
    public List<CheckWaprGate> WaprGates { get; } = [];
    /// <summary>Count from the "result N diagnostics" line, null if missing.</summary>
    public int? DiagnosticCount { get; set; }
    /// <summary>"check-config:" lines that this version of the GUI does not understand.</summary>
    public List<string> UnknownLines { get; } = [];

    public bool HasFeature(string name) => Features.Contains(name);
    public CheckChannel? Channel(int n) => Channels.FirstOrDefault(c => c.Number == n);
}
