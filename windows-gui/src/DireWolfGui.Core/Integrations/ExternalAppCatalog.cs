namespace DireWolfGui.Core.Integrations;

/// <summary>A known application and conservative guidance for connecting it to Dire Wolf.</summary>
public sealed record ExternalAppTemplate(
    string Id,
    string Name,
    string Purpose,
    TncProtocol PreferredProtocol,
    IReadOnlyList<TncProtocol> SupportedProtocols,
    IReadOnlyList<string> SetupSteps,
    string? Caveat = null)
{
    public int DefaultPort => PreferredProtocol == TncProtocol.KissTcp ? ExternalAppCatalog.DefaultKissPort : ExternalAppCatalog.DefaultAgwPort;

    public ExternalAppProfile CreateProfile(string? exePath = null) => new()
    {
        Name = Name, TemplateId = Id, ExePath = exePath, Protocol = PreferredProtocol, Host = "127.0.0.1", Port = DefaultPort,
    };
}

/// <summary>
/// Templates for applications commonly used with a sound-card TNC.  The guidance only states the
/// connection Dire Wolf offers (AGWPE-compatible AGW port, KISS over TCP, serial KISS); menu names
/// differ between versions, so it does not invent them.  Dire Wolf provides the modem/TNC only:
/// BBS, Winlink, node or DX cluster functions come from the application itself.
/// </summary>
public static class ExternalAppCatalog
{
    public const int DefaultAgwPort = 8000;
    public const int DefaultKissPort = 8001;

    private const string Agw = "configure it to use an AGWPE TNC at 127.0.0.1 port 8000";
    private const string Kiss = "configure it to use a KISS TCP TNC at 127.0.0.1 port 8001";
    private const string Docs = "Menu names differ between versions; see the application's own documentation for where the setting is.";
    private const string Running = "Start Dire Wolf first and check that the AGW/KISS port is listed as listening in the log.";

    private static ExternalAppTemplate T(string id, string name, string purpose, TncProtocol pref, TncProtocol[] supported, string? caveat, params string[] steps)
        => new(id, name, purpose, pref, supported, [Running, .. steps, Docs], caveat);

    public static IReadOnlyList<ExternalAppTemplate> All { get; } =
    [
        T("aprsisce32", "APRSISCE/32", "APRS client (map, messages, IGate features).", TncProtocol.Agwpe, [TncProtocol.Agwpe, TncProtocol.KissTcp], null,
            $"In its port setup, add an RF port and {Agw}.", $"Alternatively {Kiss}.", "Decide in APRSISCE/32 whether it may transmit (beacons, messages) on that port."),
        T("yaac", "YAAC", "Java APRS client (Yet Another APRS Client).", TncProtocol.Agwpe, [TncProtocol.Agwpe, TncProtocol.KissTcp], null,
            $"Add a port and {Agw}.", $"Alternatively {Kiss}.", "YAAC needs a Java runtime."),
        T("xastir", "Xastir", "APRS client, mainly for Linux/Unix.", TncProtocol.Agwpe, [TncProtocol.Agwpe, TncProtocol.KissTcp], "Xastir is mainly a Linux/Unix program; on Windows it is only available through a Unix environment.",
            $"Add an interface for a networked AGWPE TNC and {Agw}."),
        T("uiview32", "UI-View32", "Legacy APRS client.", TncProtocol.Agwpe, [TncProtocol.Agwpe], "UI-View32 is no longer maintained.",
            $"Use its AGWPE host mode and {Agw}."),
        T("bpq32", "BPQ32", "Packet node / BBS software (the node and BBS functions are BPQ32's, not Dire Wolf's).", TncProtocol.KissTcp, [TncProtocol.KissTcp, TncProtocol.Agwpe], null,
            $"In the BPQ32 configuration define a port that connects to a KISS TNC over TCP and {Kiss}.", $"BPQ32 can also talk to AGWPE: {Agw}."),
        T("winlink-express", "Winlink Express", "Winlink e-mail over radio (the Winlink protocol is handled by Winlink Express, not Dire Wolf).", TncProtocol.KissTcp, [TncProtocol.KissTcp], null,
            $"Open a Packet Winlink session, and in its TNC settings choose a KISS TNC and {Kiss} (if your version offers a TCP connection for KISS)."),
        T("outpost", "Outpost", "Packet message manager (connects to a BBS through the TNC).", TncProtocol.Agwpe, [TncProtocol.Agwpe], null,
            $"Choose the AGWPE TNC type and {Agw}."),
        T("pinpoint", "PinPoint APRS", "APRS client.", TncProtocol.Agwpe, [TncProtocol.Agwpe, TncProtocol.KissTcp], null,
            $"Choose an AGWPE connection and {Agw}."),
        T("sartrack", "SARTrack", "Search and rescue tracking.", TncProtocol.Agwpe, [TncProtocol.Agwpe, TncProtocol.KissTcp], null,
            $"Add a TNC connection and {Agw}.", $"If your version offers KISS over TCP instead, {Kiss}."),
        T("packet-commander", "Packet Commander", "Connected-mode packet terminal.", TncProtocol.Agwpe, [TncProtocol.Agwpe, TncProtocol.KissTcp], null,
            $"Add a TNC and {Agw}."),
        T("generic-agwpe", "Other AGWPE application", "Any program that supports an AGWPE (AGW Packet Engine) TCP connection.", TncProtocol.Agwpe, [TncProtocol.Agwpe], null,
            $"In the program, {Agw}.", "Use another computer's address only if Dire Wolf has TCPBIND ANY and the firewall allows it."),
        T("generic-kiss", "Other KISS TCP application", "Any program that supports a KISS TNC over TCP/IP.", TncProtocol.KissTcp, [TncProtocol.KissTcp], null,
            $"In the program, {Kiss}.", "KISSPORT can be limited to one radio channel (KISSPORT port channel)."),
    ];

    public static ExternalAppTemplate? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}
