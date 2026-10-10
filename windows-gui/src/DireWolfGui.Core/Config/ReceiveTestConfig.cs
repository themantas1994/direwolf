using System.Globalization;
using System.Text.RegularExpressions;

namespace DireWolfGui.Core.Config;

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
        // The test copy needs no APRS-IS passcode: mask it in every (now commented) IGLOGIN line.
        foreach (var l in doc.Lines.ToList())
        {
            string masked = PasscodeMask.MaskLine(l.Text);
            if (masked != l.Text) doc.ReplaceLine(l.Index, masked);
        }
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
