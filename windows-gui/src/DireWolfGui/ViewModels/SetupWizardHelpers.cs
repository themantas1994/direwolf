using System.Globalization;
using System.Text.RegularExpressions;
using DireWolfGui.Core.Config;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>
/// Builds the wizard's receive-only test configuration: nothing in it can make the station transmit
/// or forward.  Pure logic (no UI), kept small so it can be reviewed and tested on its own.
/// </summary>
public static class ReceiveTestConfig
{
    public const string Reason = "receive-only test by the Setup wizard";

    /// <summary>The same station options with every transmit / forward path removed.</summary>
    public static NewStationOptions ReceiveOnlyOptions(NewStationOptions o) => new()
    {
        Callsign = o.Callsign,
        AudioDevices = o.AudioDevices.Select(a => new AudioDeviceOption { Input = a.Input, Output = a.Output, Stereo = a.Stereo, SampleRate = a.SampleRate }).ToList(),
        Channels = o.Channels.Select(c => new ChannelOption
        {
            Channel = c.Channel, MyCall = c.MyCall, ModemSpeed = c.ModemSpeed, WaprProfile = c.WaprProfile, WaprAirtimePercent = c.WaprAirtimePercent,
            Ptt = PttMethod.None,
        }).ToList(),
        AgwPort = 0,
        KissPort = 0,
        AllowRemoteClients = false,
        IncludeBeacon = false,
        IncludeDigipeater = false,
        IncludeIGate = false,
        ActivateOptionalServices = false,
        NewLine = o.NewLine,
    };

    /// <summary>
    /// Post-edit a document so that it cannot transmit or forward: every directive the catalog marks as
    /// able to transmit or forward (beacons, DIGIPEAT, REGEN, IGate, WAPRGATE, PTT, serial KISS, ...) is
    /// disabled, and AGWPORT 0 / KISSPORT 0 / TCPBIND LOCAL are set so no client application can connect.
    /// </summary>
    public static ConfigDocument MakeReceiveOnly(ConfigDocument doc)
    {
        foreach (var l in doc.Directives.ToList())
        {
            if (l.Info is not { } info) continue;
            if (info.Name is "AGWPORT" or "KISSPORT") continue;
            if (info.CanTransmit || info.ForwardsTraffic) doc.DisableDirective(l.Index, Reason);
        }
        foreach (var l in doc.FindDirectives("KISSPORT").ToList())
            if (l.Arguments.FirstOrDefault() != "0") doc.DisableDirective(l.Index, Reason);
        doc.SetDirective("AGWPORT", "0");
        if (doc.FindDirective("KISSPORT") == null) doc.SetDirective("KISSPORT", "0");
        doc.SetDirective("TCPBIND", "LOCAL");
        return doc;
    }

    /// <summary>Reasons the document could still transmit or forward (empty = receive only).</summary>
    public static IReadOnlyList<string> Verify(ConfigDocument doc)
    {
        var problems = new List<string>();
        var f = ConfigFacts.From(doc);
        if (f.Beacons.Count > 0) problems.Add("a beacon is configured");
        if (f.Digipeat.Count > 0 || f.Regen.Count > 0 || f.CDigipeat.Count > 0) problems.Add("digipeating is configured");
        if (f.HasIGate || f.IGateTxChannel != null) problems.Add("the IGate is configured");
        if (f.WaprGates.Count > 0) problems.Add("a WAPR gateway rule is configured");
        if (f.AgwPort != null) problems.Add("the AGW port is open");
        if (f.KissPorts.Count > 0) problems.Add("a KISS port is open");
        if (f.SerialKiss != null) problems.Add("a serial KISS port is configured");
        if (f.Channels.Values.Any(c => c.HasPtt)) problems.Add("a PTT line is configured");
        foreach (var l in doc.Directives)
        {
            if (l.Info is not { } info || info.Name is "AGWPORT" or "KISSPORT") continue;
            if (info.CanTransmit || info.ForwardsTraffic) problems.Add($"line {l.LineNumber} ({info.Name}) can transmit or forward");
        }
        return problems.Distinct().ToList();
    }
}

/// <summary>Recognises Dire Wolf console lines during the receive test.</summary>
public static partial class ReceiveTestLines
{
    [GeneratedRegex(@"^\[\d+[\].]")]
    private static partial Regex PacketLine();

    [GeneratedRegex(@"audio level = (\d+)")]
    private static partial Regex AudioLevel();

    /// <summary>"[0] ..." or "[0.3] ...": a frame Dire Wolf received on a radio channel ("[0L]"/"[0H]" are transmissions).</summary>
    public static bool IsReceivedPacket(string line) => PacketLine().IsMatch(line);

    public static bool IsAudioLevel(string line) => line.Contains("audio level = ", StringComparison.Ordinal);

    /// <summary>The number after "audio level = ", or null.</summary>
    public static int? AudioLevelValue(string line)
    {
        var m = AudioLevel().Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : null;
    }
}

/// <summary>A step of the setup wizard.</summary>
public sealed class WizardStep(int number, string title) : ObservableObject
{
    public int Number { get; } = number;
    public string Title { get; } = title;
    public string Label => $"{Number}. {Title}";

    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    private bool _visited;
    public bool Visited { get => _visited; set => Set(ref _visited, value); }
}

/// <summary>Per-channel choices in the wizard (modem and PTT).</summary>
public sealed class WizardChannel : ObservableObject
{
    private readonly Func<bool> _waprAllowed;

    public WizardChannel(int channel, Func<bool> waprAllowed)
    {
        Channel = channel;
        _waprAllowed = waprAllowed;
    }

    public int Channel { get; }
    public string Title => $"Channel {Channel} ({(Channel % 2 == 0 ? "left or mono" : "right")} audio)";

    public IReadOnlyList<int> Speeds => ConfigTemplates.ModemSpeeds;

    private int _speed = 1200;
    public int Speed { get => _speed; set { if (Set(ref _speed, value)) OnPropertyChanged(nameof(SpeedHelp)); } }

    public string SpeedHelp => Speed switch
    {
        300 => "300 bps AFSK: HF packet (SSB).",
        1200 => "1200 bps AFSK: the usual VHF/UHF APRS and packet speed. Works through the microphone and speaker connections.",
        2400 => "2400 bps QPSK: needs a radio with flat audio response.",
        4800 => "4800 bps 8PSK: needs a radio with flat audio response.",
        _ => "9600 bps G3RUH: needs a radio with a 9600 data port (not the microphone / speaker) and a matching station on the other end.",
    };

    private bool _useWapr;
    /// <summary>Experimental WAPR, only after an explicit confirmation.</summary>
    public bool UseWapr
    {
        get => _useWapr;
        set
        {
            if (value == _useWapr) return;
            if (value)
            {
                if (!_waprAllowed()) { OnPropertyChanged(); return; }
                if (!Dialogs.Confirm(
                        $"Use the experimental WAPR modem on channel {Channel}?\n\n{Core.Wapr.WaprSupport.ExperimentalNotice}\n\n" +
                        "The whole channel becomes WAPR: AX.25 / APRS stations on that frequency will not decode it, and it does not decode them.\n\n" +
                        OperatorNotice.Responsibility, "Experimental WAPR", warning: true))
                {
                    OnPropertyChanged();
                    return;
                }
            }
            _useWapr = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> WaprProfiles { get; } = Core.Wapr.WaprSupport.Profiles.Select(p => p.Name).ToList();
    private string _waprProfile = "H150";
    public string WaprProfile { get => _waprProfile; set => Set(ref _waprProfile, value); }
    private string _waprAirtime = "";
    public string WaprAirtime { get => _waprAirtime; set => Set(ref _waprAirtime, value ?? ""); }

    public IReadOnlyList<string> PttDescriptions { get; } = ConfigTemplates.WindowsPttMethods.Select(m => m.Description).ToList();
    private int _pttIndex;
    public int PttIndex
    {
        get => _pttIndex;
        set
        {
            if (!Set(ref _pttIndex, value)) return;
            OnPropertyChanged(nameof(Ptt));
            OnPropertyChanged(nameof(IsSerial));
            OnPropertyChanged(nameof(IsCm108));
        }
    }
    public PttMethod Ptt => ConfigTemplates.WindowsPttMethods[Math.Clamp(PttIndex, 0, ConfigTemplates.WindowsPttMethods.Count - 1)].Method;
    public bool IsSerial => Ptt is PttMethod.SerialRts or PttMethod.SerialDtr;
    public bool IsCm108 => Ptt == PttMethod.Cm108;
    public IReadOnlyList<string> Ports => ConfigEdit.ComPorts;
    private string _pttPort = "COM1";
    public string PttPort { get => _pttPort; set => Set(ref _pttPort, value ?? ""); }
    private bool _pttInvert;
    public bool PttInvert { get => _pttInvert; set => Set(ref _pttInvert, value); }
    private string _cm108Gpio = "";
    public string Cm108Gpio { get => _cm108Gpio; set => Set(ref _cm108Gpio, value ?? ""); }
    private string _cm108Device = "";
    public string Cm108Device { get => _cm108Device; set => Set(ref _cm108Device, value ?? ""); }

    public ChannelOption ToOption()
    {
        double? air = null;
        if (UseWapr && WaprAirtime.Trim().Length > 0 &&
            double.TryParse(WaprAirtime.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double a)) air = a;
        return new ChannelOption
        {
            Channel = Channel,
            ModemSpeed = Speed,
            WaprProfile = UseWapr ? WaprProfile : null,
            WaprAirtimePercent = air,
            Ptt = Ptt,
            PttSerialPort = IsSerial ? PttPort.Trim().ToUpperInvariant() : null,
            PttInvert = PttInvert,
            Cm108Gpio = IsCm108 && int.TryParse(Cm108Gpio.Trim(), out int g) ? g : null,
            Cm108Device = IsCm108 && Cm108Device.Trim().Length > 0 ? Cm108Device.Trim() : null,
        };
    }
}
