namespace DireWolfGui.Core.Config;

/// <summary>Which context a directive applies to in the real parser.</summary>
public enum DirectiveScope
{
    /// <summary>Station wide.</summary>
    Global,
    /// <summary>Applies to the radio channel selected by the latest CHANNEL line (0 before any).</summary>
    Channel,
    /// <summary>Applies to the audio device selected by the latest ADEVICEn line (0 before any).</summary>
    AudioDevice,
}

public enum DirectiveCategory
{
    Audio, Channel, Modem, Ptt, Timing, Fec, Digipeater, Filter, TouchTone, IGate,
    Network, Gps, Logging, Beacon, ConnectedMode, Wapr, Obsolete,
}

/// <summary>Description of one configuration directive accepted by Dire Wolf's config_init().</summary>
public sealed record DirectiveInfo(
    string Name,
    IReadOnlyList<string> Aliases,
    DirectiveScope Scope,
    DirectiveCategory Category,
    string Summary,
    string Syntax,
    string Example,
    bool CanTransmit = false,
    bool ForwardsTraffic = false,
    bool Experimental = false,
    bool RestartRequired = true);

/// <summary>
/// Every top-level keyword accepted by src/config.c (checked by a unit test that reads config.c).
/// Syntax is taken from the comments in config.c, the conf/ samples and the User Guide.
/// </summary>
public static class DirectiveCatalog
{
    private static DirectiveInfo D(string name, DirectiveScope scope, DirectiveCategory cat, string summary, string syntax,
        string example, bool tx = false, bool fwd = false, bool exp = false, params string[] aliases)
        => new(name, aliases, scope, cat, summary, syntax, example, tx, fwd, exp);

    private const DirectiveScope G = DirectiveScope.Global, C = DirectiveScope.Channel, A = DirectiveScope.AudioDevice;

    public static IReadOnlyList<DirectiveInfo> All { get; } =
    [
        // Audio devices
        D("ADEVICE", A, DirectiveCategory.Audio, "Sound device for input and optionally a different one for output. ADEVICEn defines device n (channels 2n and 2n+1).",
            "ADEVICE[n] input-device [output-device]", "ADEVICE \"USB Audio CODEC\" \"USB Audio CODEC\"", aliases: ["ADEVICE0", "ADEVICE1", "ADEVICE2"]),
        D("PAIDEVICE", A, DirectiveCategory.Audio, "PortAudio input device name (macOS builds).", "PAIDEVICE input-device", "PAIDEVICE \"USB Audio\""),
        D("PAODEVICE", A, DirectiveCategory.Audio, "PortAudio output device name (macOS builds).", "PAODEVICE output-device", "PAODEVICE \"USB Audio\""),
        D("ARATE", A, DirectiveCategory.Audio, "Audio sample rate for the current audio device.", "ARATE samples-per-second", "ARATE 48000"),
        D("ACHANNELS", A, DirectiveCategory.Audio, "Number of audio channels for the current device: 1 (mono) or 2 (stereo, two radios).", "ACHANNELS 1|2", "ACHANNELS 2"),
        // Channels
        D("CHANNEL", G, DirectiveCategory.Channel, "Selects the radio channel that following channel settings (MYCALL, MODEM, PTT, ...) apply to.", "CHANNEL n", "CHANNEL 0"),
        D("ICHANNEL", G, DirectiveCategory.Channel, "Defines a virtual channel that lets client applications talk to APRS-IS through the IGate.", "ICHANNEL n", "ICHANNEL 9", fwd: true),
        D("NCHANNEL", G, DirectiveCategory.Channel, "Defines a virtual channel connected to a network KISS TNC.", "NCHANNEL chan host port", "NCHANNEL 10 192.168.1.20 8001", tx: true),
        D("MYCALL", C, DirectiveCategory.Channel, "Station callsign (and SSID) for the current channel; also used for channels not yet set.", "MYCALL callsign[-ssid]", "MYCALL N0CALL-1"),
        // Modem
        D("MODEM", C, DirectiveCategory.Modem, "Modem type and speed for the current channel (300, 1200, 2400, 4800, 9600, AIS, EAS, or experimental WAPR).",
            "MODEM speed [options] | MODEM WAPR F600|H150|R25 [AIRTIME=percent]", "MODEM 1200"),
        D("DTMF", C, DirectiveCategory.Modem, "Enables the DTMF (touch tone) decoder on the current channel.", "DTMF", "DTMF"),
        D("FIX_BITS", C, DirectiveCategory.Modem, "Attempts to fix frames with bad CRC by flipping bits.", "FIX_BITS n [APRS|AX25|NONE] [PASSALL]", "FIX_BITS 1 AX25"),
        D("SOFT_FIX", C, DirectiveCategory.Modem, "Soft decision bit fixing level for the current channel.", "SOFT_FIX n", "SOFT_FIX 1"),
        // PTT / control lines
        D("PTT", C, DirectiveCategory.Ptt, "How the transmitter is keyed (serial RTS/DTR, CM108 GPIO, GPIO, LPT, Hamlib).",
            "PTT COMn RTS|DTR|-RTS|-DTR [RTS|DTR] | PTT CM108 [[-]gpio] [hid-path] | PTT GPIO [-]n | PTT RIG model port", "PTT COM3 RTS", tx: true),
        D("DCD", C, DirectiveCategory.Ptt, "Output line that indicates a signal is being received.", "DCD device-or-method ...", "DCD COM3 DTR"),
        D("CON", C, DirectiveCategory.Ptt, "Output line that indicates a connected-mode session.", "CON device-or-method ...", "CON GPIO 24"),
        D("TXINH", C, DirectiveCategory.Ptt, "Input line that inhibits transmitting (GPIO only).", "TXINH GPIO [-]n", "TXINH GPIO 22"),
        // Timing
        D("DWAIT", C, DirectiveCategory.Timing, "Extra wait for receiver squelch, in 10 ms units (0-255).", "DWAIT n", "DWAIT 0"),
        D("SLOTTIME", C, DirectiveCategory.Timing, "Channel access slot time, in 10 ms units (5-49).", "SLOTTIME n", "SLOTTIME 10"),
        D("PERSIST", C, DirectiveCategory.Timing, "Channel access persistence (5-250).", "PERSIST n", "PERSIST 63"),
        D("TXDELAY", C, DirectiveCategory.Timing, "Time from PTT on to data, in 10 ms units (0-255; 10-99 sensible).", "TXDELAY n", "TXDELAY 30"),
        D("TXTAIL", C, DirectiveCategory.Timing, "Time from end of data to PTT off, in 10 ms units (0-255; 5-49 sensible).", "TXTAIL n", "TXTAIL 10"),
        D("FULLDUP", C, DirectiveCategory.Timing, "Full duplex: transmit without waiting for a clear channel.", "FULLDUP ON|OFF", "FULLDUP OFF"),
        D("SPEECH", C, DirectiveCategory.Timing, "Script used to speak text (APRStt responses) on the current channel.", "SPEECH script", "SPEECH dwespeak.bat", tx: true),
        // FEC
        D("FX25TX", C, DirectiveCategory.Fec, "Transmit FX.25 (AX.25 with forward error correction) on the current channel.", "FX25TX n", "FX25TX 1"),
        D("FX25AUTO", C, DirectiveCategory.Fec, "Automatic use of FX.25 for connected mode retries.", "FX25AUTO n", "FX25AUTO 5"),
        D("IL2PTX", C, DirectiveCategory.Fec, "Transmit IL2P instead of AX.25 on the current channel.", "IL2PTX [+|-][0|1]", "IL2PTX 1"),
        // WAPR
        D("WAPRGATE", G, DirectiveCategory.Wapr, "EXPERIMENTAL: explicit gateway rule between a WAPR channel and another channel or APRS-IS.",
            "WAPRGATE from-chan to-chan|IS [POS,STATUS,MSG,OBJ,ITEM,WX,TLM,OTHER,ALL]", "WAPRGATE 1 0 POS,MSG", tx: true, fwd: true, exp: true),
        // Digipeater
        D("DIGIPEAT", G, DirectiveCategory.Digipeater, "APRS digipeater rule: repeat matching packets from one channel to another.",
            "DIGIPEAT from-chan to-chan alias-pattern wide-pattern [OFF|DROP|MARK|TRACE]", "DIGIPEAT 0 0 ^WIDE[3-7]-[1-7]$|^TEST$ ^WIDE[12]-[12]$ TRACE",
            tx: true, fwd: true, aliases: ["DIGIPEATER"]),
        D("DEDUPE", G, DirectiveCategory.Digipeater, "Seconds during which a duplicate packet is not digipeated again (0-599).", "DEDUPE seconds", "DEDUPE 30"),
        D("REGEN", G, DirectiveCategory.Digipeater, "Retransmits everything heard on one channel on another, unchanged.", "REGEN from-chan to-chan", "REGEN 0 1", tx: true, fwd: true),
        D("CDIGIPEAT", G, DirectiveCategory.Digipeater, "Connected-mode digipeater rule.", "CDIGIPEAT from-chan to-chan [alias-pattern]", "CDIGIPEAT 0 0",
            tx: true, fwd: true, aliases: ["CDIGIPEATER"]),
        D("FILTER", G, DirectiveCategory.Filter, "Filter for digipeated or IGated packets.", "FILTER from-chan|IG to-chan|IG filter-spec", "FILTER 0 0 t/m"),
        D("CFILTER", G, DirectiveCategory.Filter, "Filter for connected-mode digipeating.", "CFILTER from-chan to-chan filter-spec", "CFILTER 0 0 t/m"),
        // APRStt
        D("TTCORRAL", G, DirectiveCategory.TouchTone, "APRStt: where to place users with unknown position.", "TTCORRAL latitude longitude offset", "TTCORRAL 37^55.50N 81^7.00W 0^0.02N"),
        D("TTPOINT", G, DirectiveCategory.TouchTone, "APRStt: a point represented by a touch tone sequence.", "TTPOINT pattern latitude longitude", "TTPOINT B01 37^55.37N 81^7.86W"),
        D("TTVECTOR", G, DirectiveCategory.TouchTone, "APRStt: location by bearing and distance.", "TTVECTOR pattern latitude longitude scale unit", "TTVECTOR B5bbbddd 37^55.37N 81^7.86W 0.01 mi"),
        D("TTGRID", G, DirectiveCategory.TouchTone, "APRStt: grid for touch tone locations.", "TTGRID pattern min-lat min-lon max-lat max-lon", "TTGRID Byyyxxx 37^50.00N 81^00.00W 37^59.99N 81^09.99W"),
        D("TTUTM", G, DirectiveCategory.TouchTone, "APRStt: UTM zone for touch tone locations.", "TTUTM pattern zone [scale [x-offset y-offset]]", "TTUTM B6xxxyyy 19T 10 300000 4720000"),
        D("TTUSNG", G, DirectiveCategory.TouchTone, "APRStt: USNG zone/square for touch tone locations.", "TTUSNG pattern zone-square", "TTUSNG B6xxxyyy 19TCH"),
        D("TTMGRS", G, DirectiveCategory.TouchTone, "APRStt: MGRS zone/square for touch tone locations.", "TTMGRS pattern zone-square", "TTMGRS B6xxxyyy 19TCH"),
        D("TTMHEAD", G, DirectiveCategory.TouchTone, "APRStt: pattern for Maidenhead locator.", "TTMHEAD pattern [prefix]", "TTMHEAD BAxxxxxx"),
        D("TTSATSQ", G, DirectiveCategory.TouchTone, "APRStt: pattern for satellite gridsquare.", "TTSATSQ pattern", "TTSATSQ BAxxxx"),
        D("TTAMBIG", G, DirectiveCategory.TouchTone, "APRStt: pattern for object location ambiguity.", "TTAMBIG pattern", "TTAMBIG BAx"),
        D("TTMACRO", G, DirectiveCategory.TouchTone, "APRStt: compact sequence with full expansion.", "TTMACRO pattern definition", "TTMACRO xx1yy B9xx*AB166*AA2B4C5B3B0A1yy"),
        D("TTOBJ", G, DirectiveCategory.TouchTone, "APRStt: where object reports are sent (radio channel and/or IGate).", "TTOBJ recv-chan where-to [via-path]", "TTOBJ 0 xmit+ig WIDE1-1", tx: true, fwd: true),
        D("TTERR", G, DirectiveCategory.TouchTone, "APRStt: response for success or errors.", "TTERR msg-id SPEECH|MORSE text", "TTERR OK SPEECH Message Received."),
        D("TTSTATUS", G, DirectiveCategory.TouchTone, "APRStt: custom status messages.", "TTSTATUS status-id text", "TTSTATUS 1 /off duty"),
        D("TTCMD", G, DirectiveCategory.TouchTone, "APRStt: command to run when a valid sequence is received.", "TTCMD command", "TTCMD ./tt-cmd.sh"),
        // IGate
        D("IGSERVER", G, DirectiveCategory.IGate, "APRS-IS server for the Internet Gateway.", "IGSERVER hostname[:port] | IGSERVER hostname [port]", "IGSERVER noam.aprs2.net", fwd: true),
        D("CWOPSERVER", G, DirectiveCategory.IGate, "CWOP (weather) server instead of APRS-IS.", "CWOPSERVER hostname[:port]", "CWOPSERVER cwop.aprs.net", fwd: true),
        D("IGLOGIN", G, DirectiveCategory.IGate, "APRS-IS login callsign and passcode (the passcode is a credential; the GUI never displays it).", "IGLOGIN callsign passcode", "IGLOGIN N0CALL-10 12345", fwd: true),
        D("IGTXVIA", G, DirectiveCategory.IGate, "Enables Internet-to-RF: radio channel and via path for packets from APRS-IS.", "IGTXVIA channel [via-path]", "IGTXVIA 0 WIDE1-1", tx: true, fwd: true),
        D("IGFILTER", G, DirectiveCategory.IGate, "Server-side filter requesting extra traffic from APRS-IS.", "IGFILTER filter-spec", "IGFILTER m/50"),
        D("IGTXLIMIT", G, DirectiveCategory.IGate, "Limit of Internet-to-RF transmissions per 1 and 5 minutes.", "IGTXLIMIT one-minute five-minute", "IGTXLIMIT 6 10"),
        D("IGMSP", G, DirectiveCategory.IGate, "Number of times to send the position of a message sender to RF (0-10).", "IGMSP n", "IGMSP 1", tx: true),
        D("SATGATE", G, DirectiveCategory.Obsolete, "Obsolete: prints a message referring to the User Guide.", "SATGATE [n]", "SATGATE"),
        // Network / clients
        D("AGWPORT", G, DirectiveCategory.Network, "TCP port of the AGW network protocol server (default 8000, 0 disables). Reachable from other computers unless TCPBIND LOCAL.",
            "AGWPORT port", "AGWPORT 8000", tx: true),
        D("KISSPORT", G, DirectiveCategory.Network, "TCP port of a KISS server (default 8001, KISSPORT 0 removes it), optionally for one channel. Reachable from other computers unless TCPBIND LOCAL.",
            "KISSPORT port [chan]", "KISSPORT 8001", tx: true),
        D("TCPBIND", G, DirectiveCategory.Network, "Network interfaces for the AGW and KISS TCP servers: LOCAL = this computer only (127.0.0.1), ANY = all interfaces (default).",
            "TCPBIND LOCAL|ANY", "TCPBIND LOCAL"),
        D("NULLMODEM", G, DirectiveCategory.Network, "Serial port (or virtual null modem) for a KISS client.", "NULLMODEM device [speed]", "NULLMODEM COM5 9600", tx: true, aliases: ["SERIALKISS"]),
        D("SERIALKISSPOLL", G, DirectiveCategory.Network, "Serial KISS port that may come and go (polled).", "SERIALKISSPOLL device", "SERIALKISSPOLL /tmp/kisstnc", tx: true),
        D("KISSCOPY", G, DirectiveCategory.Network, "Copies data from one network KISS client to all others.", "KISSCOPY", "KISSCOPY"),
        D("DNSSD", G, DirectiveCategory.Network, "Enable (1) or disable (0) DNS-SD service announcements.", "DNSSD 0|1", "DNSSD 1"),
        D("DNSSDNAME", G, DirectiveCategory.Network, "Service name for DNS-SD announcements.", "DNSSDNAME name", "DNSSDNAME \"Dire Wolf on shack\""),
        // GPS / waypoints
        D("GPSNMEA", G, DirectiveCategory.Gps, "GPS receiver on a serial port (NMEA).", "GPSNMEA device [speed]", "GPSNMEA COM7 4800"),
        D("GPSD", G, DirectiveCategory.Gps, "Uses a gpsd server (not available in all builds).", "GPSD [host [port]]", "GPSD"),
        D("WAYPOINT", G, DirectiveCategory.Gps, "Sends waypoint sentences for received positions to a serial port or UDP.", "WAYPOINT device-or-host:port [formats]", "WAYPOINT COM4 GK"),
        // Logging
        D("LOGDIR", G, DirectiveCategory.Logging, "Directory for daily CSV log files of received packets.", "LOGDIR directory", "LOGDIR logs"),
        D("LOGFILE", G, DirectiveCategory.Logging, "Single CSV log file name for received packets.", "LOGFILE path", "LOGFILE direwolf.log"),
        // Beacons
        D("BEACON", G, DirectiveCategory.Obsolete, "Obsolete form replaced by PBEACON, OBEACON, TBEACON and CBEACON (error if used).", "BEACON ...", "BEACON"),
        D("PBEACON", G, DirectiveCategory.Beacon, "Periodic position beacon.", "PBEACON keyword=value ... (delay, every, sendto, via, lat, long, symbol, comment, ...)",
            "PBEACON delay=1 every=30 symbol=\"digi\" lat=42^37.14N long=071^20.83W", tx: true),
        D("OBEACON", G, DirectiveCategory.Beacon, "Periodic object beacon.", "OBEACON objname=name keyword=value ...", "OBEACON objname=WX lat=42^37.14N long=071^20.83W symbol=\"/_\"", tx: true),
        D("TBEACON", G, DirectiveCategory.Beacon, "Tracker beacon using GPS position (SmartBeaconing if configured).", "TBEACON keyword=value ...", "TBEACON every=2 via=WIDE1-1,WIDE2-1", tx: true),
        D("CBEACON", G, DirectiveCategory.Beacon, "Custom beacon with arbitrary information part.", "CBEACON info=text keyword=value ...", "CBEACON every=60 info=\">Station status\"", tx: true),
        D("IBEACON", G, DirectiveCategory.Beacon, "IGate statistics beacon (sent to APRS-IS or radio).", "IBEACON keyword=value ...", "IBEACON sendto=IG every=60", tx: true),
        D("SMARTBEACON", G, DirectiveCategory.Beacon, "SmartBeaconing parameters for TBEACON.", "SMARTBEACON [fast_speed fast_rate slow_speed slow_rate turn_time turn_angle turn_slope]",
            "SMARTBEACON 60 1:30 5 30:00 0:15 30 255", aliases: ["SMARTBEACONING"]),
        // Connected mode
        D("FRACK", G, DirectiveCategory.ConnectedMode, "Seconds to wait for an acknowledgement (connected mode).", "FRACK seconds", "FRACK 4"),
        D("RETRY", G, DirectiveCategory.ConnectedMode, "Retries before giving up (connected mode).", "RETRY n", "RETRY 10"),
        D("PACLEN", G, DirectiveCategory.ConnectedMode, "Maximum information part length (connected mode).", "PACLEN n", "PACLEN 128"),
        D("MAXFRAME", G, DirectiveCategory.ConnectedMode, "Window size, modulo 8 (connected mode).", "MAXFRAME n", "MAXFRAME 4"),
        D("EMAXFRAME", G, DirectiveCategory.ConnectedMode, "Window size, modulo 128 (connected mode).", "EMAXFRAME n", "EMAXFRAME 30"),
        D("MAXV22", G, DirectiveCategory.ConnectedMode, "SABME attempts before falling back to AX.25 v2.0.", "MAXV22 n", "MAXV22 3"),
        D("V20", G, DirectiveCategory.ConnectedMode, "Stations known to support only AX.25 v2.0.", "V20 address [address ...]", "V20 N0CALL-5"),
        D("NOXID", G, DirectiveCategory.ConnectedMode, "Stations known not to understand XID.", "NOXID address [address ...]", "NOXID N0CALL-5"),
    ];

    private static readonly Dictionary<string, DirectiveInfo> ByName = BuildIndex();

    private static Dictionary<string, DirectiveInfo> BuildIndex()
    {
        var d = new Dictionary<string, DirectiveInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in All)
        {
            d[i.Name] = i;
            foreach (var a in i.Aliases) d[a] = i;
        }
        return d;
    }

    /// <summary>Find a directive by name or alias (case-insensitive). Like the real parser,
    /// any keyword starting with ADEVICE is the ADEVICE directive.</summary>
    public static DirectiveInfo? Find(string keyword)
    {
        if (ByName.TryGetValue(keyword, out var info)) return info;
        if (keyword.StartsWith("ADEVICE", StringComparison.OrdinalIgnoreCase)) return ByName["ADEVICE"];
        return null;
    }

    public static bool IsKnown(string keyword) => Find(keyword) != null;

    /// <summary>All names and aliases.</summary>
    public static IEnumerable<string> AllKeywords => All.SelectMany(i => i.Aliases.Prepend(i.Name));
}
