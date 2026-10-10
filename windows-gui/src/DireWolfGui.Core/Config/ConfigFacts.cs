using System.Globalization;

namespace DireWolfGui.Core.Config;

/// <summary>A beacon line with the options the GUI cares about (intervals in seconds).</summary>
public sealed record BeaconFacts(int Line, string Kind, string SendTo, int Channel, int DelaySeconds, int EverySeconds, string? Via, IReadOnlyDictionary<string, string> Options)
{
    public bool ToRadio => SendTo == "XMIT";
    public bool ToInternet => SendTo == "IGATE";
}

/// <summary>Per-channel facts gathered from the document.</summary>
public sealed class ChannelFacts
{
    public int Number { get; init; }
    public string? MyCall { get; set; }
    public int? MyCallLine { get; set; }
    public string? Modem { get; set; }
    public int? ModemLine { get; set; }
    public string? WaprProfile { get; set; }
    public double? WaprAirtime { get; set; }
    public bool HasPtt { get; set; }
    public int TxDelay { get; set; } = 30;
    public int TxTail { get; set; } = 10;
    public bool IsWapr => string.Equals(Modem, "WAPR", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a configuration document sets up, interpreted with the real parser's ordering rules
/// (device / channel validity is decided when the CHANNEL line is read).  Shared by
/// <see cref="ConfigValidator"/> and <see cref="StationServicesAnalyzer"/>.
/// </summary>
public sealed class ConfigFacts
{
    public const int MaxAudioDevices = 3;
    public const int MaxRadioChannels = 6;
    public const int MaxTotalChannels = 16;
    public const int DefaultAgwPort = 8000;
    public const int DefaultKissPort = 8001;

    /// <summary>Audio device number → number of audio channels (1 or 2). Device 0 always exists.</summary>
    public Dictionary<int, int> AudioDevices { get; } = new() { [0] = 1 };
    public HashSet<int> DefinedDevices { get; } = [];
    public SortedDictionary<int, ChannelFacts> Channels { get; } = new();
    public HashSet<int> VirtualChannels { get; } = [];
    public int? AgwPort { get; set; } = DefaultAgwPort;
    public int? AgwPortLine { get; set; }
    public List<(int Port, int Channel, int? Line)> KissPorts { get; } = [(DefaultKissPort, -1, null)];
    /// <summary>True when TCPBIND LOCAL is in effect (last TCPBIND line).</summary>
    public bool BindLocalOnly { get; set; }
    public string? SerialKiss { get; set; }
    public List<(int From, int To, int Line)> Digipeat { get; } = [];
    public List<(int From, int To, int Line)> Regen { get; } = [];
    public List<(int From, int To, int Line)> CDigipeat { get; } = [];
    public string? IGateServer { get; set; }
    public int? IGateServerLine { get; set; }
    public string? IGateLogin { get; set; }
    public int? IGateLoginLine { get; set; }
    public int? IGateTxChannel { get; set; }
    public string? IGateTxVia { get; set; }
    public int? IGateTxViaLine { get; set; }
    public List<BeaconFacts> Beacons { get; } = [];
    public List<(string From, string To, string? Types, int Line)> WaprGates { get; } = [];

    public bool HasIGate => IGateServer != null;
    /// <summary>Radio channels that are valid at the end of the file.</summary>
    public IEnumerable<int> RadioChannels => Channels.Keys.Where(IsRadioChannel);

    /// <summary>Channel validity per the real parser: the device n/2 is defined and, for odd channels, stereo.</summary>
    public bool IsRadioChannel(int ch) => ch >= 0 && ch < MaxRadioChannels && AudioDevices.TryGetValue(ch / 2, out int n) && (ch % 2 == 0 || n == 2);

    public ChannelFacts Channel(int n) => Channels.TryGetValue(n, out var c) ? c : Channels[n] = new ChannelFacts { Number = n };

    public static ConfigFacts From(ConfigDocument doc)
    {
        var f = new ConfigFacts();
        int adev = 0;
        f.Channel(0);
        foreach (var l in doc.Directives)
        {
            var a = l.AllTokens;
            string? a0 = a.Count > 0 ? a[0] : null;
            switch (l.Directive)
            {
                case "ADEVICE":
                    adev = l.AudioDeviceDefined ?? 0;
                    if (adev < MaxAudioDevices) { f.DefinedDevices.Add(adev); f.AudioDevices.TryAdd(adev, 1); f.Channel(adev * 2); }
                    break;
                case "PAIDEVICE" or "PAODEVICE": adev = 0; f.DefinedDevices.Add(0); break;
                case "ACHANNELS" when a0 is "1" or "2":
                    if (adev < MaxAudioDevices)
                    {
                        f.AudioDevices[adev] = a0 == "2" ? 2 : 1;
                        f.Channel(adev * 2);
                        if (a0 == "2") f.Channel(adev * 2 + 1);
                    }
                    break;
                case "MYCALL" when a0 != null:
                    // Sets the current channel and every channel not yet set (or still NOCALL/N0CALL).
                    foreach (var c in Enumerable.Range(0, MaxRadioChannels))
                    {
                        var cf = f.Channel(c);
                        if (c == l.Channel || cf.MyCall == null || IsPlaceholderCall(cf.MyCall, exactOnly: true))
                        {
                            cf.MyCall = a0.ToUpperInvariant();
                            if (c == l.Channel || cf.MyCallLine == null) cf.MyCallLine = l.LineNumber;
                        }
                    }
                    break;
                case "MODEM" when a0 != null:
                {
                    var cf = f.Channel(l.Channel);
                    cf.ModemLine = l.LineNumber;
                    if (string.Equals(a0, "WAPR", StringComparison.OrdinalIgnoreCase))
                    {
                        cf.Modem = "WAPR";
                        cf.WaprProfile = a.Count > 1 ? a[1].ToUpperInvariant() : null;
                        cf.WaprAirtime = a.Skip(2).Select(t => Wapr.WaprSupport.TryParseAirtime(t, out var p) ? p : (double?)null).LastOrDefault(p => p != null);
                    }
                    else { cf.Modem = a0.ToUpperInvariant(); cf.WaprProfile = null; cf.WaprAirtime = null; }
                    break;
                }
                case "PTT" when a0 != null: f.Channel(l.Channel).HasPtt = true; break;
                case "TXDELAY" when TryInt(a0, out int td): f.Channel(l.Channel).TxDelay = td; break;
                case "TXTAIL" when TryInt(a0, out int tt): f.Channel(l.Channel).TxTail = tt; break;
                case "ICHANNEL" when TryInt(a0, out int ic): f.VirtualChannels.Add(ic); break;
                case "NCHANNEL" when TryInt(a0, out int nc): f.VirtualChannels.Add(nc); break;
                case "AGWPORT" when TryInt(a0, out int ap): f.AgwPort = ap == 0 ? null : ap; f.AgwPortLine = l.LineNumber; break;
                case "KISSPORT" when TryInt(a0, out int kp):
                    if (kp == 0) f.KissPorts.RemoveAll(k => k.Line == null);
                    else
                    {
                        f.KissPorts.RemoveAll(k => k.Port == kp);
                        f.KissPorts.Add((kp, a.Count > 1 && TryInt(a[1], out int kc) ? kc : -1, l.LineNumber));
                    }
                    break;
                case "TCPBIND" when a0 != null:
                    if (a0.Equals("LOCAL", StringComparison.OrdinalIgnoreCase)) f.BindLocalOnly = true;
                    else if (a0.Equals("ANY", StringComparison.OrdinalIgnoreCase)) f.BindLocalOnly = false;
                    break;
                case "NULLMODEM" or "SERIALKISSPOLL" when a0 != null: f.SerialKiss = a0; break;
                case "DIGIPEAT" when a.Count >= 2 && TryInt(a[0], out int df) && TryInt(a[1], out int dt): f.Digipeat.Add((df, dt, l.LineNumber)); break;
                case "REGEN" when a.Count >= 2 && TryInt(a[0], out int rf) && TryInt(a[1], out int rt): f.Regen.Add((rf, rt, l.LineNumber)); break;
                case "CDIGIPEAT" when a.Count >= 2 && TryInt(a[0], out int cf2) && TryInt(a[1], out int ct): f.CDigipeat.Add((cf2, ct, l.LineNumber)); break;
                case "IGSERVER" or "CWOPSERVER" when a0 != null: f.IGateServer = a0; f.IGateServerLine = l.LineNumber; break;
                case "IGLOGIN" when a0 != null: f.IGateLogin = a0; f.IGateLoginLine = l.LineNumber; break;
                case "IGTXVIA" when TryInt(a0, out int iv):
                    f.IGateTxChannel = iv; f.IGateTxVia = a.Count > 1 ? a[1] : null; f.IGateTxViaLine = l.LineNumber; break;
                case "PBEACON" or "OBEACON" or "TBEACON" or "CBEACON" or "IBEACON":
                    f.Beacons.Add(ParseBeacon(l));
                    break;
                case "WAPRGATE":
                    f.WaprGates.Add((a0 ?? "", a.Count > 1 ? a[1] : "", a.Count > 2 ? a[2] : null, l.LineNumber));
                    break;
            }
        }
        return f;
    }

    public static bool TryInt(string? s, out int value)
    {
        value = 0;
        return s != null && s.Length > 0 && s.All(char.IsAsciiDigit) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>N0CALL / NOCALL (with any SSID unless <paramref name="exactOnly"/>).</summary>
    public static bool IsPlaceholderCall(string? call, bool exactOnly = false)
    {
        if (string.IsNullOrWhiteSpace(call)) return true;
        string c = call.Trim().ToUpperInvariant();
        if (!exactOnly) c = c.Split('-')[0];
        return c is "N0CALL" or "NOCALL";
    }

    /// <summary>Minutes or minutes:seconds, as parse_interval() in config.c.</summary>
    public static int ParseInterval(string s)
    {
        int colon = s.IndexOf(':');
        static int Atoi(string x) { int n = 0, i = 0; while (i < x.Length && char.IsAsciiDigit(x[i])) n = n * 10 + (x[i++] - '0'); return n; }
        return colon >= 0 ? Atoi(s[..colon]) * 60 + Atoi(s[(colon + 1)..]) : Atoi(s) * 60;
    }

    private static BeaconFacts ParseBeacon(ConfigLine l)
    {
        var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in l.AllTokens)
        {
            int eq = t.IndexOf('=');
            if (eq > 0) opts[t[..eq]] = t[(eq + 1)..];
        }
        string sendTo = "XMIT";
        int chan = 0;
        if (opts.TryGetValue("sendto", out var st) && st.Length > 0)
        {
            char c = char.ToUpperInvariant(st[0]);
            if (c == 'I') sendTo = "IGATE";
            else if (c == 'R') { sendTo = "RECV"; TryInt(st[1..], out chan); }
            else if (c is 'T' or 'X') TryInt(st[1..], out chan);
            else TryInt(st, out chan);
        }
        int delay = opts.TryGetValue("delay", out var d) ? ParseInterval(d) : 60;
        int every = opts.TryGetValue("every", out var e) ? ParseInterval(e) : 600;
        return new BeaconFacts(l.LineNumber, l.Directive!, sendTo, chan, delay, every, opts.GetValueOrDefault("via"), opts);
    }
}
