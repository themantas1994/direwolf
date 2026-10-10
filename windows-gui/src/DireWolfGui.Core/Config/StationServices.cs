using DireWolfGui.Core.Wapr;

namespace DireWolfGui.Core.Config;

public enum StationServiceKind { Beacon, Digipeater, Regenerator, ConnectedDigipeater, IGateRfToInternet, IGateInternetToRf, WaprGate, ClientPort, SerialClient }

/// <summary>Something the configuration makes the station do that sends or relays traffic.</summary>
public sealed record StationService(
    StationServiceKind Kind,
    string Title,
    string Explanation,
    bool Transmits,
    bool Forwards,
    int? Line = null,
    int? Channel = null);

public sealed record StationServicesReport(IReadOnlyList<StationService> Services, IReadOnlyList<ConfigDiagnostic> Warnings)
{
    public bool TransmitsAnything => Services.Any(s => s.Transmits);
    public bool ForwardsAnything => Services.Any(s => s.Forwards);
}

/// <summary>
/// Lists, in plain language, what a configuration can TRANSMIT or FORWARD: beacons, digipeating,
/// IGate in both directions, WAPR gates and client applications (which can transmit through AGW/KISS).
/// Uses the document; when a <see cref="CheckConfigSummary"/> from the real parser is given it is
/// preferred for ports and the tcpbind setting.
/// </summary>
public static class StationServicesAnalyzer
{
    /// <summary>Beacons more often than this to a radio channel get a warning (APRS convention: 10 min fixed, 30 min typical).</summary>
    public static readonly TimeSpan MinRecommendedBeaconInterval = TimeSpan.FromMinutes(10);

    public static StationServicesReport Analyze(ConfigDocument doc, CheckConfigSummary? summary = null)
    {
        var f = ConfigFacts.From(doc);
        var s = new List<StationService>();
        var w = new List<ConfigDiagnostic>();
        void Warn(int? line, string code, string msg) => w.Add(new ConfigDiagnostic(DiagnosticSeverity.Warning, line, code, msg));

        // Beacons
        foreach (var b in f.Beacons)
        {
            string every = Interval(b.EverySeconds);
            string what = b.Kind switch
            {
                "PBEACON" => "position beacon", "OBEACON" => "object beacon", "TBEACON" => "tracker (GPS) beacon",
                "CBEACON" => "custom beacon", "IBEACON" => "IGate statistics beacon", _ => "beacon",
            };
            if (b.ToInternet)
                s.Add(new(StationServiceKind.Beacon, $"{Cap(what)} to APRS-IS", $"Sends a {what} to the Internet (APRS-IS) through the IGate every {every}.", false, true, b.Line));
            else if (b.SendTo == "RECV")
                s.Add(new(StationServiceKind.Beacon, $"{Cap(what)} (simulated receive)", $"Pretends to receive a {what} on channel {b.Channel} every {every}; nothing is transmitted by the beacon itself, but it may be digipeated or IGated.", false, true, b.Line, b.Channel));
            else
            {
                bool wapr = f.IsRadioChannel(b.Channel) && f.Channel(b.Channel).IsWapr;
                s.Add(new(StationServiceKind.Beacon, $"{Cap(what)} on channel {b.Channel}",
                    $"Transmits a {what} on radio channel {b.Channel}{(wapr ? " (WAPR)" : "")} every {every}, first after {Interval(b.DelaySeconds)}{(b.Via != null ? $", via {b.Via}" : "")}.", true, false, b.Line, b.Channel));
                if (b.EverySeconds > 0 && b.EverySeconds < MinRecommendedBeaconInterval.TotalSeconds && b.Kind != "TBEACON")
                    Warn(b.Line, "beacon-rate", $"This beacon transmits every {every}. On shared APRS channels, every 10 minutes or more (30 minutes for a fixed station) is the usual courtesy.");
                if (wapr && WaprSupport.FindProfile(f.Channel(b.Channel).WaprProfile) is { } p && b.EverySeconds > 0)
                {
                    var est = WaprSupport.EstimateDuty(p, 3600.0 / b.EverySeconds, f.Channel(b.Channel).TxDelay, f.Channel(b.Channel).TxTail, f.Channel(b.Channel).WaprAirtime);
                    if (est.Level >= DutyLevel.Elevated) Warn(b.Line, "wapr-duty", est.Explanation);
                }
            }
        }

        // Digipeater, regenerator, connected digipeater
        foreach (var (from, to, line) in f.Digipeat)
        {
            s.Add(new(StationServiceKind.Digipeater, from == to ? $"APRS digipeater on channel {from}" : $"APRS digipeater channel {from} → {to}",
                from == to ? $"Repeats packets heard on channel {from} that ask for this digipeater (matching alias or WIDEn-N path) on the same channel."
                           : $"Repeats matching packets heard on channel {from} on channel {to} (cross-band digipeating).", true, true, line, to));
            if (from != to && f.Digipeat.Any(x => x.From == to && x.To == from))
                Warn(line, "digi-crossband-loop", $"Digipeating both {from} → {to} and {to} → {from}: make sure the alias/WIDE patterns and DEDUPE prevent packets bouncing between the channels.");
        }
        foreach (var (from, to, line) in f.Regen)
        {
            s.Add(new(StationServiceKind.Regenerator, $"Regenerator channel {from} → {to}", $"Retransmits EVERYTHING heard on channel {from} on channel {to}, unchanged (no duplicate or path checks).", true, true, line, to));
            if (from == to) Warn(line, "regen-loop", $"REGEN {from} {to} retransmits on the channel it listens to: every packet would be sent again and again.");
            else if (f.Regen.Any(x => x.From == to && x.To == from)) Warn(line, "regen-loop", $"REGEN in both directions between channels {from} and {to} forms a loop.");
        }
        foreach (var (from, to, line) in f.CDigipeat)
            s.Add(new(StationServiceKind.ConnectedDigipeater, $"Connected-mode digipeater channel {from} → {to}", $"Relays connected-mode (non-APRS) frames addressed through this station from channel {from} to channel {to}.", true, true, line, to));

        // IGate
        string? server = summary?.IGate?.Server ?? f.IGateServer;
        if (server != null)
        {
            s.Add(new(StationServiceKind.IGateRfToInternet, "IGate: radio → Internet", $"Sends APRS packets heard on radio channels to APRS-IS ({server}). Nothing is transmitted on the radio by this direction.", false, true, f.IGateServerLine));
            int? txChan = summary?.IGate is { TxChannel: >= 0 } ig ? ig.TxChannel : f.IGateTxChannel;
            if (txChan is int tc)
            {
                s.Add(new(StationServiceKind.IGateInternetToRf, $"IGate: Internet → radio channel {tc}",
                    $"Transmits selected traffic from APRS-IS (messages to stations heard nearby) on channel {tc}{(f.IGateTxVia != null ? $" via {f.IGateTxVia}" : "")}.", true, true, f.IGateTxViaLine, tc));
                if (doc.FindDirective("IGTXLIMIT") == null)
                    Warn(f.IGateTxViaLine, "igate-txlimit", "Internet-to-radio is enabled; Dire Wolf's default IGTXLIMIT applies. Review it so the radio channel is not flooded.");
                if (f.IGateTxVia != null && f.IGateTxVia.Contains("WIDE2", StringComparison.OrdinalIgnoreCase))
                    Warn(f.IGateTxViaLine, "igate-path", $"IGTXVIA path {f.IGateTxVia} spreads Internet traffic widely; WIDE1-1 or no path is usual.");
            }
            if (f.Digipeat.Count > 0)
                Warn(f.IGateServerLine, "igate-and-digi", "This station is both an IGate and a digipeater. That is common, but double-check the IGTXVIA path and digipeater aliases so Internet traffic is not digipeated further than intended.");
        }

        // WAPR gates
        if (summary?.WaprGates.Count > 0 || f.WaprGates.Count > 0)
        {
            foreach (var (from, to, types, line) in f.WaprGates)
            {
                bool toIs = to.Equals("IS", StringComparison.OrdinalIgnoreCase);
                int bits = WaprSupport.ParseGateTypes(types);
                s.Add(new(StationServiceKind.WaprGate, $"WAPR gate {from} → {(toIs ? "APRS-IS" : to)}",
                    $"EXPERIMENTAL: forwards {WaprSupport.DescribeGateTypes(bits)} frames heard on channel {from} to {(toIs ? "APRS-IS through the IGate" : $"radio channel {to}")}.",
                    !toIs, true, line, toIs ? null : ConfigFacts.TryInt(to, out int t) ? t : null));
            }
        }

        // Client applications
        bool local = summary?.TcpBindReported == true ? summary.BindLocalOnly : f.BindLocalOnly;
        int? agw = summary != null ? summary.AgwPort : f.AgwPort;
        var kiss = summary != null ? summary.KissPorts.Select(k => (k.Port, k.Channel)).ToList() : f.KissPorts.Select(k => (k.Port, k.Channel)).ToList();
        string reach = local ? "from programs on this computer only (TCPBIND LOCAL)" : "from other computers on the network as well as this one";
        if (agw is int a)
            s.Add(new(StationServiceKind.ClientPort, $"AGW port {a}", $"Client applications can connect {reach} and transmit on any radio channel.", true, false, f.AgwPortLine));
        foreach (var (port, ch) in kiss)
            s.Add(new(StationServiceKind.ClientPort, $"KISS TCP port {port}", $"Client applications can connect {reach} and transmit on {(ch < 0 ? "any radio channel" : $"channel {ch}")}.", true, false,
                f.KissPorts.FirstOrDefault(k => k.Port == port).Line));
        if ((agw != null || kiss.Count > 0) && !local)
            Warn(f.AgwPortLine, "network-exposed", "AGW/KISS ports are reachable from other computers: anyone who can connect can transmit with your callsign. Add TCPBIND LOCAL unless remote clients are needed.");
        string? serial = summary?.SerialKiss?.Device ?? f.SerialKiss;
        if (serial != null)
            s.Add(new(StationServiceKind.SerialClient, $"Serial KISS {serial}", $"An application on serial port {serial} can transmit through Dire Wolf.", true, false));

        return new StationServicesReport(s, w);
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    public static string Interval(int seconds) =>
        seconds % 3600 == 0 && seconds >= 3600 ? $"{seconds / 3600} h" : seconds % 60 == 0 ? $"{seconds / 60} min" : seconds < 60 ? $"{seconds} s" : $"{seconds / 60} min {seconds % 60} s";
}
