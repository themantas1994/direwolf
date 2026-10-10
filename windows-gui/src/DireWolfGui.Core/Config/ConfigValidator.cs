using DireWolfGui.Core.Wapr;

namespace DireWolfGui.Core.Config;

/// <summary>
/// GUI-side static checks of a configuration, with line numbers.  These complement (never replace)
/// the real parser run by <see cref="ConfigChecker"/>; a few checks (WAPRGATE sides, network exposure,
/// overridden settings) are things --check-config does not report.
/// </summary>
public static class ConfigValidator
{
    private static readonly HashSet<string> SingleValued = new(StringComparer.Ordinal)
    {
        "MYCALL", "MODEM", "TXDELAY", "TXTAIL", "DWAIT", "SLOTTIME", "PERSIST", "FULLDUP", "FX25TX", "IL2PTX", "PTT",
        "ARATE", "ACHANNELS", "AGWPORT", "IGSERVER", "IGLOGIN", "IGTXVIA", "TCPBIND", "LOGDIR", "LOGFILE", "DEDUPE",
    };

    public static IReadOnlyList<ConfigDiagnostic> Validate(ConfigDocument doc, CheckConfigSummary? summary = null)
    {
        var d = new List<ConfigDiagnostic>();
        void Add(DiagnosticSeverity s, int? line, string code, string msg) => d.Add(new ConfigDiagnostic(s, line, code, msg));
        const DiagnosticSeverity E = DiagnosticSeverity.Error, W = DiagnosticSeverity.Warning, I = DiagnosticSeverity.Info;

        var facts = ConfigFacts.From(doc);

        // ---- line by line, in file order (channel validity depends on order, as in config_init)
        var devices = new Dictionary<int, int> { [0] = 1 };
        var definedBy = new Dictionary<int, int>();
        int adev = 0;
        var seen = new Dictionary<(string, int?), int>();
        foreach (var l in doc.Directives)
        {
            var a = l.AllTokens;
            string? a0 = a.Count > 0 ? a[0] : null;
            int n = l.LineNumber;
            if (l.Info == null)
            {
                Add(E, n, "unknown-directive", $"Unrecognized command '{ConfigTokenizer.Split(l.Text)[0]}'. Dire Wolf ignores this line.");
                continue;
            }
            if (l.TrailingComment != null)
                Add(W, n, "inline-comment", $"Dire Wolf has no comments after a setting: \"{l.TrailingComment.Trim()}\" is read as extra parameters of {l.Directive}. Put the comment on its own line.");

            int? ctx = l.Info.Scope switch { DirectiveScope.Channel => l.Channel, DirectiveScope.AudioDevice => l.AudioDeviceDefined ?? adev, _ => null };
            if (SingleValued.Contains(l.Directive!))
            {
                if (seen.TryGetValue((l.Directive!, ctx), out int prev))
                    Add(W, n, "overridden", $"{l.Directive}{(l.Info.Scope == DirectiveScope.Channel ? $" for channel {ctx}" : "")} is also set on line {prev}; this later line wins.");
                seen[(l.Directive!, ctx)] = n;
            }

            switch (l.Directive)
            {
                case "ADEVICE":
                    adev = l.AudioDeviceDefined ?? 0;
                    if (adev >= ConfigFacts.MaxAudioDevices) { Add(E, n, "adevice-range", $"Audio device number {adev} is out of range (0-{ConfigFacts.MaxAudioDevices - 1})."); adev = 0; break; }
                    if (a0 == null) Add(E, n, "adevice-missing", "ADEVICE needs the name or number of an audio device. Dire Wolf stops on this error.");
                    if (definedBy.TryGetValue(adev, out int first)) Add(E, n, "adevice-twice", $"Audio device {adev} is already defined on line {first}.");
                    definedBy[adev] = n;
                    devices.TryAdd(adev, 1);
                    break;
                case "PAIDEVICE" or "PAODEVICE": adev = 0; break;
                case "ACHANNELS":
                    if (a0 is "1" or "2") devices[adev] = a0 == "2" ? 2 : 1;
                    else Add(E, n, "achannels", "ACHANNELS must be 1 (mono) or 2 (stereo).");
                    break;
                case "ARATE":
                    if (!ConfigFacts.TryInt(a0, out int rate) || rate < 8000 || rate > 192000) Add(E, n, "arate", "ARATE must be a sample rate such as 44100 or 48000.");
                    break;
                case "CHANNEL":
                    if (!ConfigFacts.TryInt(a0, out int ch) || ch >= ConfigFacts.MaxRadioChannels)
                        Add(E, n, "channel-range", $"CHANNEL must be a number from 0 to {ConfigFacts.MaxRadioChannels - 1}.");
                    else if (!devices.TryGetValue(ch / 2, out int nch))
                        Add(E, n, "channel-no-device", $"Channel {ch} is not valid because audio device {ch / 2} is not defined (add ADEVICE{ch / 2} before this line).");
                    else if (ch % 2 == 1 && nch != 2)
                        Add(E, n, "channel-not-stereo", $"Channel {ch} is not valid because audio device {ch / 2} is not in stereo (add ACHANNELS 2 after its ADEVICE line, before this line).");
                    break;
                case "MYCALL":
                    if (a0 == null) Add(E, n, "mycall-missing", "MYCALL needs a callsign.");
                    else if (ConfigFacts.IsPlaceholderCall(a0)) Add(W, n, "mycall-placeholder", $"MYCALL {a0} is a placeholder. Use your own callsign before transmitting.");
                    else if (!IsValidCall(a0)) Add(E, n, "mycall-invalid", $"\"{a0}\" is not a valid callsign (1-6 letters/digits, optional -SSID 0-15).");
                    break;
                case "MODEM":
                    ValidateModem(l, a, d, facts, summary);
                    break;
                case "TXDELAY": Range(l, a0, 0, 255, d, 10, 99, "TXDELAY is in 10 ms units; 10-99 is sensible (30 = 300 ms)."); break;
                case "TXTAIL": Range(l, a0, 0, 255, d, 5, 49, "TXTAIL is in 10 ms units; 5-49 is sensible (10 = 100 ms)."); break;
                case "DWAIT": Range(l, a0, 0, 255, d); break;
                case "SLOTTIME": Range(l, a0, 5, 49, d); break;
                case "PERSIST": Range(l, a0, 5, 250, d); break;
                case "FULLDUP":
                    if (a0 == null || !(a0.Equals("ON", StringComparison.OrdinalIgnoreCase) || a0.Equals("OFF", StringComparison.OrdinalIgnoreCase)))
                        Add(E, n, "fulldup", "FULLDUP must be ON or OFF.");
                    break;
                case "FX25TX" or "IL2PTX" when facts.Channel(l.Channel).IsWapr:
                    Add(W, n, "fec-on-wapr", $"{l.Directive} does not apply to channel {l.Channel}, which is a WAPR channel (MODEM WAPR). Remove it or move it to an AX.25 channel.");
                    break;
                case "AGWPORT" or "KISSPORT":
                    if (!ConfigFacts.TryInt(a0, out int port) || (port != 0 && (port < 1024 || port > 49151)))
                        Add(E, n, "port-range", $"{l.Directive} port must be 0 (off) or 1024-49151.");
                    break;
                case "TCPBIND":
                    if (a0 == null || !(a0.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) || a0.Equals("ANY", StringComparison.OrdinalIgnoreCase)))
                        Add(E, n, "tcpbind", "TCPBIND must be followed by LOCAL or ANY.");
                    break;
                case "WAPRGATE": break; // after the loop: needs all MODEM lines.
                case "BEACON": Add(E, n, "beacon-obsolete", "Old style BEACON is no longer accepted; use PBEACON, OBEACON, TBEACON or CBEACON."); break;
                case "SATGATE": Add(W, n, "satgate", "SATGATE is obsolete and has no effect."); break;
                case "IGLOGIN":
                    if (a.Count < 2) Add(E, n, "iglogin", "IGLOGIN needs a callsign and an APRS-IS passcode.");
                    break;
                case "PBEACON" or "OBEACON" or "TBEACON" or "CBEACON" or "IBEACON":
                    foreach (var t in a.Where(t => !t.Contains('=')))
                        Add(E, n, "beacon-option", $"\"{t}\" has no '=': beacon options are keyword=value.");
                    break;
            }
        }

        // ---- whole-file checks
        bool anyMyCall = doc.FindDirectives("MYCALL").Any();
        if (!anyMyCall)
            Add(W, null, "mycall-none", "No MYCALL line: set your callsign before transmitting (beacons, digipeating, IGate and client applications use it).");

        foreach (var (wf, wt, wty, line) in facts.WaprGates) ValidateWaprGate(wf, wt, wty, line, facts, d);

        if (facts.IGateLogin != null && facts.IGateServer == null)
            Add(W, facts.IGateLoginLine, "iglogin-no-server", "IGLOGIN is set but there is no IGSERVER, so the IGate is not used.");
        if (facts.IGateServer != null && facts.IGateLogin == null)
            Add(E, facts.IGateServerLine, "igserver-no-login", "IGSERVER needs an IGLOGIN line (callsign and passcode) for the IGate to log in.");
        if (facts.IGateTxChannel is int itx && facts.IGateServer == null)
            Add(W, facts.IGateTxViaLine, "igtxvia-no-server", "IGTXVIA is set but there is no IGSERVER.");
        if (facts.IGateTxChannel is int itc && !facts.IsRadioChannel(itc))
            Add(E, facts.IGateTxViaLine, "igtxvia-channel", $"IGTXVIA channel {itc} is not a radio channel.");

        foreach (var (from, to, line) in facts.Digipeat.Concat(facts.CDigipeat))
        {
            if (!facts.IsRadioChannel(from) && !facts.VirtualChannels.Contains(from)) Add(E, line, "digi-channel", $"Digipeater FROM-channel {from} is not a valid channel.");
            if (!facts.IsRadioChannel(to) && !facts.VirtualChannels.Contains(to)) Add(E, line, "digi-channel", $"Digipeater TO-channel {to} is not a valid channel.");
            if (facts.IsRadioChannel(to) && ConfigFacts.IsPlaceholderCall(facts.Channel(to).MyCall, exactOnly: true))
                Add(E, line, "digi-mycall", $"MYCALL must be set for transmit channel {to} before digipeating is allowed.");
            if (!facts.Beacons.Any(b => b.Channel == to && b.SendTo != "IGATE"))
                Add(W, line, "digi-no-beacon", $"Beaconing should be configured for channel {to} when digipeating is enabled (other stations learn about the digipeater from its beacon).");
            if (facts.IsRadioChannel(from) && facts.IsRadioChannel(to) && (facts.Channel(from).IsWapr || facts.Channel(to).IsWapr))
                Add(W, line, "digi-wapr", "Digipeating does not involve WAPR channels; use WAPRGATE rules for WAPR.");
        }

        foreach (var b in facts.Beacons)
        {
            if (b.SendTo is "XMIT" or "RECV" && !facts.IsRadioChannel(b.Channel) && !facts.VirtualChannels.Contains(b.Channel))
                Add(E, b.Line, "beacon-channel", $"Beacon sends to channel {b.Channel}, which is not a valid channel.");
            if (b.ToInternet && !facts.HasIGate)
                Add(W, b.Line, "beacon-igate", "Beacon sends to the IGate (sendto=IG) but no IGSERVER is configured.");
        }

        // Ports: exposure and duplicates.
        var tcp = new List<(int Port, string What, int? Line)>();
        if (facts.AgwPort is int ap) tcp.Add((ap, "AGW", facts.AgwPortLine));
        tcp.AddRange(facts.KissPorts.Select(k => (k.Port, "KISS TCP", k.Line)));
        foreach (var g in tcp.GroupBy(t => t.Port).Where(g => g.Select(x => x.What).Distinct().Count() > 1))
            Add(E, g.Max(x => x.Line), "port-duplicate", $"TCP port {g.Key} is used for both {string.Join(" and ", g.Select(x => x.What).Distinct())}.");
        if (tcp.Count > 0)
        {
            string list = string.Join(", ", tcp.Select(t => $"{t.What} {t.Port}"));
            if (facts.BindLocalOnly)
                Add(I, doc.FindDirective("TCPBIND")?.LineNumber, "network-local", $"Client ports ({list}) accept connections from this computer only (TCPBIND LOCAL).");
            else
                Add(W, tcp.Select(t => t.Line).FirstOrDefault(x => x != null), "network-exposed",
                    $"Client ports ({list}) are reachable from other computers on the network, and any client that connects can transmit. " +
                    "Add TCPBIND LOCAL unless other computers need them, and keep the firewall closed to untrusted networks.");
        }

        if (summary != null && !summary.HasFeature("wapr") && facts.Channels.Values.Any(c => c.IsWapr))
            Add(E, facts.Channels.Values.First(c => c.IsWapr).ModemLine, "wapr-unsupported", WaprSupport.NotSupportedExplanation);

        return d.OrderBy(x => x.Line ?? int.MaxValue).ThenByDescending(x => x.Severity).ToList();
    }

    private static void ValidateModem(ConfigLine l, IReadOnlyList<string> a, List<ConfigDiagnostic> d, ConfigFacts facts, CheckConfigSummary? summary)
    {
        int n = l.LineNumber;
        if (a.Count == 0) { d.Add(new(DiagnosticSeverity.Error, n, "modem-missing", "MODEM needs a speed (e.g. 1200) or WAPR and a profile.")); return; }
        if (a[0].Equals("WAPR", StringComparison.OrdinalIgnoreCase))
        {
            if (a.Count < 2 || !WaprSupport.IsValidProfile(a[1]))
            {
                d.Add(new(DiagnosticSeverity.Error, n, "wapr-profile", "MODEM WAPR needs a profile name: F600, H150 or R25."));
                return;
            }
            foreach (var opt in a.Skip(2))
                if (!WaprSupport.TryParseAirtime(opt, out _))
                    d.Add(new(DiagnosticSeverity.Warning, n, "wapr-option", $"Option \"{opt}\" after MODEM WAPR is ignored. Only AIRTIME=percent (more than 0, at most 100)."));
            d.Add(new(DiagnosticSeverity.Info, n, "wapr-experimental", $"Channel {l.Channel} is an EXPERIMENTAL WAPR channel. " + WaprSupport.ExperimentalNotice));
            return;
        }
        if (a[0].Equals("AIS", StringComparison.OrdinalIgnoreCase) || a[0].Equals("EAS", StringComparison.OrdinalIgnoreCase)) return;
        if (!ConfigFacts.TryInt(a[0], out int baud) || baud < 100 || baud > 40000)
            d.Add(new(DiagnosticSeverity.Error, n, "modem-speed", $"\"{a[0]}\" is not a usable data rate (e.g. 300, 1200, 2400, 4800, 9600)."));
        else if (baud is not (300 or 1200 or 2400 or 4800 or 9600 or 19200))
            d.Add(new(DiagnosticSeverity.Warning, n, "modem-nonstandard", $"Non-standard data rate of {baud} bits per second."));
    }

    private static void ValidateWaprGate(string from, string to, string? types, int line, ConfigFacts f, List<ConfigDiagnostic> d)
    {
        void Err(string code, string m) => d.Add(new ConfigDiagnostic(DiagnosticSeverity.Error, line, code, m));
        if (from.Equals("IS", StringComparison.OrdinalIgnoreCase)) { Err("waprgate-from-is", "WAPRGATE from APRS-IS (IS) to WAPR is not supported."); return; }
        bool toIs = to.Equals("IS", StringComparison.OrdinalIgnoreCase);
        if (!ConfigFacts.TryInt(from, out int fc) || (!toIs && !ConfigFacts.TryInt(to, out _)))
        {
            Err("waprgate-syntax", "WAPRGATE needs a from channel and a to channel (or IS).");
            return;
        }
        int tc = toIs ? -1 : int.Parse(to);
        if (WaprSupport.ParseGateTypes(types) == 0) { Err("waprgate-types", "WAPRGATE types must be from POS, STATUS, MSG, OBJ, ITEM, WX, TLM, OTHER, ALL (comma separated)."); return; }
        if (!f.IsRadioChannel(fc) || (!toIs && !f.IsRadioChannel(tc)) || fc == tc)
        {
            Err("waprgate-channels", $"WAPRGATE {from} {to}: both must be different radio channels (or IS). Dire Wolf ignores this rule.");
            return;
        }
        bool fromW = f.Channel(fc).IsWapr, toW = !toIs && f.Channel(tc).IsWapr;
        if (!fromW && !toW) { Err("waprgate-no-wapr", $"WAPRGATE {from} {to}: neither side is a MODEM WAPR channel. Dire Wolf ignores this rule."); return; }
        if (toIs && !f.HasIGate) Err("waprgate-no-igate", "WAPRGATE ... IS needs the IGate (IGSERVER and IGLOGIN) to be configured.");
        d.Add(new ConfigDiagnostic(DiagnosticSeverity.Info, line, "waprgate-forwards",
            $"Experimental WAPR gateway: frames heard on channel {fc} ({(fromW ? "WAPR" : "AX.25")}) are forwarded to {(toIs ? "APRS-IS" : $"channel {tc} ({(toW ? "WAPR" : "AX.25")})")}."));
    }

    private static void Range(ConfigLine l, string? v, int min, int max, List<ConfigDiagnostic> d, int? sensibleMin = null, int? sensibleMax = null, string? hint = null)
    {
        if (!ConfigFacts.TryInt(v, out int n) || n < min || n > max)
        {
            d.Add(new(DiagnosticSeverity.Error, l.LineNumber, "range", $"{l.Directive} must be a whole number from {min} to {max}; Dire Wolf will use its default."));
            return;
        }
        if ((sensibleMin is int lo && n < lo) || (sensibleMax is int hi && n > hi))
            d.Add(new(DiagnosticSeverity.Warning, l.LineNumber, "range-unusual", $"{l.Directive} {n} is unusual. {hint}"));
    }

    /// <summary>AX.25 address: 1-6 letters/digits and optional -SSID 0-15.</summary>
    public static bool IsValidCall(string call)
    {
        var parts = call.Split('-');
        if (parts.Length > 2 || parts[0].Length is < 1 or > 6 || !parts[0].All(char.IsAsciiLetterOrDigit)) return false;
        return parts.Length == 1 || (ConfigFacts.TryInt(parts[1], out int ssid) && ssid <= 15 && parts[1].Length <= 2);
    }
}
