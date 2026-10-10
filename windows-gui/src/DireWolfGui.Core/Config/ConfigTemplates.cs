using System.Globalization;
using System.Text;
using DireWolfGui.Core.Wapr;

namespace DireWolfGui.Core.Config;

/// <summary>PTT methods offered by the wizard: only those a Windows build of Dire Wolf supports
/// (serial RTS/DTR, CM108 GPIO since 1.7).  GPIO, LPT and Hamlib are not offered.</summary>
public enum PttMethod
{
    /// <summary>Receive only: no PTT line.</summary>
    None,
    /// <summary>The radio keys itself on audio (VOX / interface with VOX): no PTT line.</summary>
    Vox,
    /// <summary>PTT COMn RTS (or -RTS inverted).</summary>
    SerialRts,
    /// <summary>PTT COMn DTR (or -DTR inverted).</summary>
    SerialDtr,
    /// <summary>PTT CM108 [gpio] [HID path]: GPIO pin of a C-Media USB audio adapter.</summary>
    Cm108,
}

public sealed class AudioDeviceOption
{
    /// <summary>Input device: Windows name (or part of it, up to 31 characters) or number, e.g. "USB Audio CODEC" or "1".</summary>
    public string Input { get; set; } = "0";
    /// <summary>Output device; null = same as input.</summary>
    public string? Output { get; set; }
    /// <summary>Two radios on the left and right audio channels (ACHANNELS 2).</summary>
    public bool Stereo { get; set; }
    public int? SampleRate { get; set; }
}

public sealed class ChannelOption
{
    public int Channel { get; set; }
    /// <summary>Callsign for this channel; null = station callsign.</summary>
    public string? MyCall { get; set; }
    /// <summary>300, 1200, 2400, 4800 or 9600.  Ignored when <see cref="WaprProfile"/> is set.</summary>
    public int ModemSpeed { get; set; } = 1200;
    /// <summary>Experimental WAPR profile (F600, H150, R25); only when explicitly chosen.</summary>
    public string? WaprProfile { get; set; }
    public double? WaprAirtimePercent { get; set; }
    public PttMethod Ptt { get; set; } = PttMethod.None;
    /// <summary>Serial port for SerialRts/SerialDtr, e.g. "COM3".</summary>
    public string? PttSerialPort { get; set; }
    /// <summary>Inverted serial line (-RTS / -DTR) or inverted CM108 GPIO.</summary>
    public bool PttInvert { get; set; }
    /// <summary>CM108 GPIO pin (default 3).</summary>
    public int? Cm108Gpio { get; set; }
    /// <summary>CM108 HID device path (on Windows \\?\hid#vid_0d8c&amp;...); null = automatic.</summary>
    public string? Cm108Device { get; set; }
}

public sealed class NewStationOptions
{
    public string Callsign { get; set; } = "";
    public List<AudioDeviceOption> AudioDevices { get; set; } = [new()];
    public List<ChannelOption> Channels { get; set; } = [new()];
    /// <summary>AGW port; 0 disables.</summary>
    public int AgwPort { get; set; } = 8000;
    /// <summary>KISS TCP port; 0 disables (writes KISSPORT 0).</summary>
    public int KissPort { get; set; } = 8001;
    /// <summary>false (default): TCPBIND LOCAL, clients on this computer only.  true: TCPBIND ANY.</summary>
    public bool AllowRemoteClients { get; set; }

    /// <summary>Include a position beacon section (commented out unless <see cref="ActivateOptionalServices"/>).</summary>
    public bool IncludeBeacon { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int BeaconEveryMinutes { get; set; } = 30;
    public string BeaconComment { get; set; } = "";
    /// <summary>Include a digipeater section (commented out unless activated).</summary>
    public bool IncludeDigipeater { get; set; }
    /// <summary>Include an IGate section (commented out unless activated).  The passcode is NOT written.</summary>
    public bool IncludeIGate { get; set; }
    public string IGateServer { get; set; } = "noam.aprs2.net";
    /// <summary>APRS-IS passcode, written to the configuration file only (Dire Wolf reads it from there) and only
    /// when <see cref="ActivateOptionalServices"/> is set; never stored in GUI settings.</summary>
    public string? IGatePasscode { get; set; }
    /// <summary>Emit the requested beacon/digipeater/IGate lines active instead of commented out.  Default false.</summary>
    public bool ActivateOptionalServices { get; set; }
    public string NewLine { get; set; } = "\r\n";
}

/// <summary>Starter configurations for the setup wizard: well commented, and safe — nothing that
/// transmits on its own (beacons, digipeating, IGate) is active unless explicitly requested.</summary>
public static class ConfigTemplates
{
    public static IReadOnlyList<int> ModemSpeeds { get; } = [300, 1200, 2400, 4800, 9600];

    /// <summary>PTT methods with a note about Windows support (from src/ptt.c, src/config.c and CMakeLists.txt).</summary>
    public static IReadOnlyList<(PttMethod Method, string Description)> WindowsPttMethods { get; } =
    [
        (PttMethod.None, "No PTT (receive only)."),
        (PttMethod.Vox, "VOX: the radio or interface keys itself when it hears audio. No PTT line is written."),
        (PttMethod.SerialRts, "Serial port RTS line (PTT COMn RTS), e.g. a USB serial interface. Supported on Windows."),
        (PttMethod.SerialDtr, "Serial port DTR line (PTT COMn DTR). Supported on Windows."),
        (PttMethod.Cm108, "GPIO pin of a C-Media CM108/CM119 USB audio adapter (PTT CM108). Supported by Windows builds since Dire Wolf 1.7; if the device is not found automatically, give its HID path (\\\\?\\hid#vid_0d8c&...)."),
    ];

    public static string NewStation(NewStationOptions o)
    {
        string call = (o.Callsign ?? "").Trim().ToUpperInvariant();
        if (!ConfigValidator.IsValidCall(call)) throw new ArgumentException($"\"{o.Callsign}\" is not a valid callsign (1-6 letters/digits, optional -SSID 0-15).");
        if (o.AudioDevices.Count is < 1 or > ConfigFacts.MaxAudioDevices) throw new ArgumentException("One to three audio devices are supported.");
        foreach (var c in o.Channels)
        {
            int dev = c.Channel / 2;
            if (c.Channel < 0 || dev >= o.AudioDevices.Count || (c.Channel % 2 == 1 && !o.AudioDevices[dev].Stereo))
                throw new ArgumentException($"Channel {c.Channel} needs audio device {dev}{(c.Channel % 2 == 1 ? " in stereo" : "")}.");
            if (c.WaprProfile != null && !WaprSupport.IsValidProfile(c.WaprProfile)) throw new ArgumentException($"Unknown WAPR profile \"{c.WaprProfile}\".");
            if (c.WaprProfile == null && !ModemSpeeds.Contains(c.ModemSpeed)) throw new ArgumentException($"Modem speed {c.ModemSpeed} is not offered.");
            if (c.Ptt is PttMethod.SerialRts or PttMethod.SerialDtr && string.IsNullOrWhiteSpace(c.PttSerialPort)) throw new ArgumentException($"Channel {c.Channel}: serial PTT needs a port such as COM3.");
            if (c.MyCall != null && !ConfigValidator.IsValidCall(c.MyCall.Trim().ToUpperInvariant())) throw new ArgumentException($"\"{c.MyCall}\" is not a valid callsign.");
        }
        if (o.Channels.Select(c => c.Channel).Distinct().Count() != o.Channels.Count) throw new ArgumentException("A channel is listed twice.");

        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append(o.NewLine);
        void Section(string title) { L(); L("#############################################################"); L("# " + title); L("#############################################################"); L(); }
        bool on = o.ActivateOptionalServices;
        string opt = on ? "" : "#";

        L("# Dire Wolf configuration created by Dire Wolf Station " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".");
        L("# Lines starting with # are comments.  Dire Wolf does not allow comments after a setting");
        L("# on the same line, so every explanation is on a line of its own.");
        L("# Changes take effect when Dire Wolf is restarted.");

        Section("Audio devices");
        L("# ADEVICE input [output]: Windows sound devices by number or by (part of) their name,");
        L("# as listed when Dire Wolf starts.  ADEVICEn defines device n, which provides radio");
        L("# channels 2n and 2n+1 (the second only with ACHANNELS 2 = stereo, two radios).");
        for (int i = 0; i < o.AudioDevices.Count; i++)
        {
            var a = o.AudioDevices[i];
            L();
            string outDev = string.IsNullOrWhiteSpace(a.Output) ? a.Input : a.Output!;
            L($"{(i == 0 ? "ADEVICE" : $"ADEVICE{i}")} {ConfigTokenizer.Quote(a.Input.Trim())} {ConfigTokenizer.Quote(outDev.Trim())}");
            if (a.SampleRate is int r) L($"ARATE {r}");
            L($"ACHANNELS {(a.Stereo ? 2 : 1)}");
        }

        foreach (var c in o.Channels.OrderBy(c => c.Channel))
        {
            Section($"Radio channel {c.Channel}");
            L($"CHANNEL {c.Channel}");
            L();
            L("# Your callsign (with optional SSID) for this channel.");
            L($"MYCALL {(c.MyCall ?? call).Trim().ToUpperInvariant()}");
            L();
            if (c.WaprProfile != null)
            {
                var p = WaprSupport.FindProfile(c.WaprProfile)!;
                L("# EXPERIMENTAL WAPR modem, chosen explicitly.  This whole channel becomes WAPR: it is");
                L("# NOT compatible with AX.25 / APRS radios, TNCs or digipeaters and only talks to other");
                L("# WAPR stations.  32 byte information part at most, no digipeater path.  Simulation-only");
                L("# validation so far; on-air performance is unverified.  See doc/wapr/USAGE.md.");
                L($"# Profile {p.Name}: {p.Use}, {p.SymbolRate:0} Bd, {p.ToneDescription}, {p.FrameSeconds:0.##} s per frame.");
                if (c.WaprAirtimePercent is double air)
                {
                    L($"# AIRTIME={air.ToString("0.##", CultureInfo.InvariantCulture)}: transmit at most that share of the time (10 minute average).");
                    L($"MODEM WAPR {p.Name} AIRTIME={air.ToString("0.##", CultureInfo.InvariantCulture)}");
                }
                else L($"MODEM WAPR {p.Name}");
            }
            else
            {
                L(c.ModemSpeed switch
                {
                    300 => "# 300 bps AFSK (HF packet).",
                    1200 => "# 1200 bps AFSK: the usual VHF/UHF APRS and packet speed.",
                    2400 => "# 2400 bps QPSK.  Needs a radio with flat audio response.",
                    4800 => "# 4800 bps 8PSK.  Needs a radio with flat audio response.",
                    _ => "# 9600 bps (G3RUH).  Needs a radio with a 9600 data port, not the mic / speaker.",
                });
                L($"MODEM {c.ModemSpeed}");
            }
            L();
            switch (c.Ptt)
            {
                case PttMethod.None:
                    L("# No PTT: this channel only receives.  Add a PTT line to transmit, e.g.");
                    L("#PTT COM3 RTS");
                    break;
                case PttMethod.Vox:
                    L("# PTT: VOX.  The radio or interface keys the transmitter when it hears audio,");
                    L("# so no PTT line is needed.  Keep TXDELAY long enough for VOX to react.");
                    break;
                case PttMethod.SerialRts or PttMethod.SerialDtr:
                {
                    string line = c.Ptt == PttMethod.SerialRts ? "RTS" : "DTR";
                    L($"# PTT on serial port {c.PttSerialPort!.Trim().ToUpperInvariant()}, {line} line{(c.PttInvert ? ", inverted (active low)" : "")}.");
                    L($"PTT {c.PttSerialPort.Trim().ToUpperInvariant()} {(c.PttInvert ? "-" : "")}{line}");
                    break;
                }
                case PttMethod.Cm108:
                {
                    L("# PTT through a GPIO pin of a CM108/CM119 USB audio adapter (default GPIO 3).");
                    L("# Supported by Windows builds of Dire Wolf since 1.7.  If Dire Wolf cannot find the");
                    L("# adapter by itself, add its HID path (\\\\?\\hid#vid_0d8c&...) at the end of the line.");
                    var parts = new List<string> { "PTT", "CM108" };
                    if (c.Cm108Gpio is int g || c.PttInvert) parts.Add((c.PttInvert ? "-" : "") + (c.Cm108Gpio ?? 3).ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrWhiteSpace(c.Cm108Device)) parts.Add(c.Cm108Device.Trim());
                    L(string.Join(" ", parts));
                    break;
                }
            }
            if (c.Ptt != PttMethod.None)
            {
                L();
                L("# Transmit timing in 10 ms units: 30 = 300 ms from PTT on to data, 10 = 100 ms tail.");
                L("TXDELAY 30");
                L("TXTAIL 10");
            }
        }

        Section("Client applications (AGW and KISS over TCP)");
        L("# Programs such as APRS clients connect here.  Any program that connects can TRANSMIT");
        L("# with your callsign.");
        if (o.AllowRemoteClients)
        {
            L("# TCPBIND ANY: other computers on the network can connect too.  Only use this on a");
            L("# trusted network and keep these ports closed in the firewall otherwise.");
            L("TCPBIND ANY");
        }
        else
        {
            L("# TCPBIND LOCAL: only programs on this computer can connect (127.0.0.1).  Change to");
            L("# TCPBIND ANY only if programs on other computers must connect.");
            L("TCPBIND LOCAL");
        }
        L();
        L(o.AgwPort > 0 ? "# AGW network protocol (AGWPE compatible) port." : "# AGW network protocol server disabled.");
        L($"AGWPORT {o.AgwPort}");
        L(o.KissPort > 0 ? "# KISS over TCP port." : "# KISS over TCP server disabled.");
        L($"KISSPORT {o.KissPort}");

        if (o.IncludeBeacon)
        {
            Section("Position beacon" + (on ? "" : " (disabled: remove the # to enable)"));
            L("# Transmits your position on channel 0.  Every 30 minutes is plenty for a fixed station.");
            string lat = o.Latitude is double la ? la.ToString("0.0000", CultureInfo.InvariantCulture) : "0.0000";
            string lon = o.Longitude is double lo ? lo.ToString("0.0000", CultureInfo.InvariantCulture) : "0.0000";
            if (o.Latitude == null || o.Longitude == null) L("# Set lat= and long= to your position (decimal degrees, south and west negative).");
            int ch = o.Channels.OrderBy(c => c.Channel).First().Channel;
            string comment = string.IsNullOrWhiteSpace(o.BeaconComment) ? "" : " " + ConfigTokenizer.Quote("comment=" + o.BeaconComment.Trim());
            L($"{opt}PBEACON delay=1 every={o.BeaconEveryMinutes} sendto={ch} symbol=\"/-\" lat={lat} long={lon}{comment}");
        }
        if (o.IncludeDigipeater)
        {
            Section("APRS digipeater" + (on ? "" : " (disabled: remove the # to enable)"));
            L("# Repeats packets on channel 0 that ask for WIDE1-1 / WIDE2-n, with TRACE.  Only run a");
            L("# digipeater where it is needed, and beacon so others know it exists.");
            L($"{opt}DIGIPEAT 0 0 ^WIDE[3-7]-[1-7]$|^TEST$ ^WIDE[12]-[12]$ TRACE");
        }
        if (o.IncludeIGate)
        {
            Section("Internet Gateway (IGate)" + (on ? "" : " (disabled: remove the # to enable)"));
            L("# Sends packets heard on the radio to APRS-IS.  IGLOGIN needs your APRS-IS passcode.");
            bool igOn = on && !string.IsNullOrWhiteSpace(o.IGatePasscode);
            if (!igOn) L("# Replace PASSCODE with your own and remove the # from both lines to enable.");
            L($"{(igOn ? "" : "#")}IGSERVER {o.IGateServer}");
            L($"{(igOn ? "" : "#")}IGLOGIN {call} {(igOn ? o.IGatePasscode!.Trim() : "PASSCODE")}");
            L("# Internet to radio (transmits!) stays disabled unless you remove the # below.");
            L("#IGTXVIA 0 WIDE1-1");
            L("#IGTXLIMIT 6 10");
        }
        return sb.ToString();
    }
}
