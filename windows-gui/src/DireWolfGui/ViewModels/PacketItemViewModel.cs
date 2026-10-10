using System.Globalization;
using System.Text;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Packets;

namespace DireWolfGui.ViewModels;

/// <summary>Plain-words labels and theme colours for where a packet came from / went to.</summary>
public static class PacketOriginText
{
    /// <summary>Short chip text; always distinct, so the colour is never the only cue.</summary>
    public static string Short(PacketDirection d, PacketOrigin o) => (d, o) switch
    {
        (PacketDirection.Received, PacketOrigin.Radio) => "RF rx",
        (PacketDirection.Transmitted, PacketOrigin.Local) => "TX (this station)",
        (_, PacketOrigin.FromAprsIs) => "From APRS-IS",
        (_, PacketOrigin.ToAprsIs) => "To APRS-IS",
        (_, PacketOrigin.IgateToRadio) => "IGate → RF",
        (_, PacketOrigin.Gateway) => "WAPR gateway",
        (_, PacketOrigin.NetworkTnc) => "Network TNC",
        (_, PacketOrigin.Dtmf) => "DTMF",
        (_, PacketOrigin.Ais) => "AIS",
        (PacketDirection.Transmitted, _) => "TX",
        _ => "RX",
    };

    /// <summary>Longer explanation for tooltips and the detail panel.</summary>
    public static string Long(PacketDirection d, PacketOrigin o) => (d, o) switch
    {
        (PacketDirection.Received, PacketOrigin.Radio) => "Received over the radio",
        (PacketDirection.Transmitted, PacketOrigin.Local) => "Transmitted on the radio by this station",
        (_, PacketOrigin.FromAprsIs) => "Received from the APRS-IS server (internet), not heard on the radio",
        (_, PacketOrigin.ToAprsIs) => "Sent to the APRS-IS server by this station's IGate",
        (_, PacketOrigin.IgateToRadio) => "Relayed from APRS-IS to the radio by this station's IGate (transmitted)",
        (_, PacketOrigin.Gateway) => "Relayed by a WAPR gateway",
        (_, PacketOrigin.NetworkTnc) => "Sent by a network TNC client (AGW/KISS application) through Dire Wolf",
        (_, PacketOrigin.Dtmf) => "DTMF tones decoded on the radio channel",
        (_, PacketOrigin.Ais) => "AIS (ship) message received",
        (PacketDirection.Transmitted, _) => "Transmitted",
        _ => "Received",
    };

    /// <summary>Theme brush prefix for <c>ThemeBrush</c> (e.g. "Rx" → RxBrush).</summary>
    public static string Kind(PacketDirection d, PacketOrigin o) => o switch
    {
        PacketOrigin.Radio when d == PacketDirection.Received => "Rx",
        PacketOrigin.Local => "Tx",
        PacketOrigin.FromAprsIs or PacketOrigin.ToAprsIs => "IgateColor",
        PacketOrigin.IgateToRadio => "Tx",
        PacketOrigin.Gateway => "Gateway",
        PacketOrigin.NetworkTnc => "Accent",
        _ => d == PacketDirection.Transmitted ? "Tx" : "Neutral",
    };

    public static string TypeText(PacketRecord p) =>
        p.FrameDescription is { } fd ? fd
        : p.Aprs is { } a ? AprsTypeText(a.Type)
        : "";

    public static string AprsTypeText(AprsPacketType t) => t switch
    {
        AprsPacketType.Position => "Position",
        AprsPacketType.PositionWithTimestamp => "Position (timestamp)",
        AprsPacketType.MicE => "Mic-E",
        AprsPacketType.Object => "Object",
        AprsPacketType.Item => "Item",
        AprsPacketType.Message => "Message",
        AprsPacketType.MessageAck => "Message ack",
        AprsPacketType.MessageReject => "Message reject",
        AprsPacketType.Bulletin => "Bulletin",
        AprsPacketType.Status => "Status",
        AprsPacketType.Weather => "Weather",
        AprsPacketType.Telemetry => "Telemetry",
        AprsPacketType.Query => "Query",
        AprsPacketType.ThirdParty => "Third party",
        _ => "Other",
    };

    /// <summary>One display line: control characters shown as Dire Wolf style &lt;0xNN&gt;, long text cut.</summary>
    public static string OneLine(string text, int max = 300)
    {
        if (text.Length == 0) return text;
        StringBuilder? sb = null;
        for (int i = 0; i < text.Length && i < max; i++)
        {
            char c = text[i];
            if (c < 0x20 || c == 0x7F)
            {
                sb ??= new StringBuilder(text, 0, i, Math.Min(text.Length, max) + 16);
                sb.Append("<0x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)).Append('>');
            }
            else sb?.Append(c);
        }
        var s = sb?.ToString() ?? (text.Length > max ? text[..max] : text);
        return text.Length > max ? s + "…" : s;
    }
}

/// <summary>One row of the packet monitor. Immutable except for data Dire Wolf adds later (decode lines, raw bytes).</summary>
public sealed class PacketItemViewModel
{
    private string? _info;

    public PacketItemViewModel(PacketRecord packet)
    {
        Packet = packet;
        TimeText = packet.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        OriginText = PacketOriginText.Short(packet.Direction, packet.Origin);
        OriginKind = PacketOriginText.Kind(packet.Direction, packet.Origin);
        OriginToolTip = PacketOriginText.Long(packet.Direction, packet.Origin);
        TypeText = PacketOriginText.TypeText(packet);
    }

    public PacketRecord Packet { get; }
    public long Sequence => Packet.Sequence;
    public DateTimeOffset Time => Packet.Time;
    public string TimeText { get; }
    public string ChannelText => Packet.Label;
    public string OriginText { get; }
    public string OriginKind { get; }
    public string OriginToolTip { get; }
    public string Source => Packet.Source;
    public string Destination => Packet.Destination;
    public string PathText => Packet.PathText;
    public string TypeText { get; }
    public string AudioText => Packet.AudioLevelText ?? Packet.AudioLevel?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string FecText => Packet.IsWapr && Packet.WaprDetails is { } w ? "WAPR " + w : Packet.Fec ?? "";
    public string InfoText => _info ??= PacketOriginText.OneLine(Packet.Info);
    public bool IsTransmitted => Packet.Direction == PacketDirection.Transmitted;

    /// <summary>A plain text line for copying / text export.</summary>
    public string ToTextLine() =>
        $"{Packet.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} [{Packet.Label}] {OriginText}: {Packet.ToMonitorString()}"
        + (Packet.AudioLevelText is { } a ? $"  (audio {a})" : "") + (Packet.Fec is { } f ? $"  {f}" : "");
}
