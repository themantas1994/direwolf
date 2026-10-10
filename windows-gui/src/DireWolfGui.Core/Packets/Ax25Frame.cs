using System.Text;

namespace DireWolfGui.Core.Packets;

/// <summary>An AX.25 address: callsign, SSID and the H ("has been repeated") / C bit (bit 7 of the SSID byte).</summary>
public readonly record struct Ax25Address(string Callsign, int Ssid, bool HBit = false)
{
    /// <summary>"CALL", "CALL-n", with a trailing '*' when <see cref="HBit"/> is set and <paramref name="markRepeated"/>.</summary>
    public string ToString(bool markRepeated) => (Ssid == 0 ? Callsign : $"{Callsign}-{Ssid}") + (markRepeated && HBit ? "*" : "");
    public override string ToString() => ToString(false);

    /// <summary>Parses "CALL", "CALL-12" or "WIDE2-1*" (trailing '*' sets the H bit).</summary>
    public static Ax25Address Parse(string text)
    {
        if (!TryParse(text, out var a)) throw new FormatException($"Invalid AX.25 address \"{text}\".");
        return a;
    }

    public static bool TryParse(string? text, out Ax25Address address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim().ToUpperInvariant();
        bool h = text.EndsWith('*');
        if (h) text = text[..^1];
        int ssid = 0;
        int dash = text.IndexOf('-');
        string call = text;
        if (dash >= 0)
        {
            call = text[..dash];
            if (!int.TryParse(text[(dash + 1)..], out ssid) || ssid < 0 || ssid > 15) return false;
        }
        if (call.Length < 1 || call.Length > 6) return false;
        foreach (char c in call) if (!char.IsAsciiLetterOrDigit(c)) return false;
        address = new Ax25Address(call, ssid, h);
        return true;
    }
}

public enum Ax25FrameType { I, RR, RNR, REJ, SREJ, SABME, SABM, DISC, DM, UA, FRMR, UI, XID, TEST, UnknownU }

/// <summary>
/// Decoded AX.25 frame (without FCS), modulo 8 control field.
/// Describes the control field in the same words Dire Wolf uses, e.g. "UI cmd, p=0" or "I cmd, n(s)=1, n(r)=2, p=0, pid=0xf0".
/// </summary>
public sealed class Ax25Frame
{
    public const int MaxRepeaters = 8;

    public Ax25Frame(Ax25Address destination, Ax25Address source, IReadOnlyList<Ax25Address> repeaters, byte control, byte? pid, byte[] info)
    {
        if (repeaters.Count > MaxRepeaters) throw new ArgumentException("At most 8 repeaters.", nameof(repeaters));
        Destination = destination;
        Source = source;
        Repeaters = repeaters;
        Control = control;
        Pid = pid;
        Info = info;
    }

    public Ax25Address Destination { get; }
    public Ax25Address Source { get; }
    public IReadOnlyList<Ax25Address> Repeaters { get; }
    public byte Control { get; }
    public byte? Pid { get; }
    public byte[] Info { get; }

    public Ax25FrameType FrameType =>
        (Control & 1) == 0 ? Ax25FrameType.I :
        (Control & 3) == 1 ? ((Control >> 2) & 3) switch { 0 => Ax25FrameType.RR, 1 => Ax25FrameType.RNR, 2 => Ax25FrameType.REJ, _ => Ax25FrameType.SREJ } :
        (Control & 0xEF) switch
        {
            0x6F => Ax25FrameType.SABME, 0x2F => Ax25FrameType.SABM, 0x43 => Ax25FrameType.DISC, 0x0F => Ax25FrameType.DM,
            0x63 => Ax25FrameType.UA, 0x87 => Ax25FrameType.FRMR, 0x03 => Ax25FrameType.UI, 0xAF => Ax25FrameType.XID,
            0xE3 => Ax25FrameType.TEST, _ => Ax25FrameType.UnknownU,
        };

    public bool PollFinal => (Control & 0x10) != 0;
    public int? NR => FrameType is Ax25FrameType.I or Ax25FrameType.RR or Ax25FrameType.RNR or Ax25FrameType.REJ or Ax25FrameType.SREJ ? (Control >> 5) & 7 : null;
    public int? NS => FrameType == Ax25FrameType.I ? (Control >> 1) & 7 : null;

    /// <summary>"cmd", "res", "cc=00" or "cc=11" from the C bits of destination and source.</summary>
    public string CommandResponse => (Destination.HBit, Source.HBit) switch
    {
        (true, false) => "cmd", (false, true) => "res", (true, true) => "cc=11", _ => "cc=00",
    };

    /// <summary>A UI frame with PID 0xF0, the kind of frame APRS uses.</summary>
    public bool IsUiNoLayer3 => FrameType == Ax25FrameType.UI && Pid == 0xF0;

    public string Describe()
    {
        string pf = CommandResponse switch { "cmd" => "p", "res" => "f", _ => "p/f" };
        int p = PollFinal ? 1 : 0;
        return FrameType switch
        {
            Ax25FrameType.I => $"I {CommandResponse}, n(s)={NS}, n(r)={NR}, {pf}={p}, pid=0x{Pid ?? 0:x2}",
            Ax25FrameType.RR or Ax25FrameType.RNR or Ax25FrameType.REJ or Ax25FrameType.SREJ => $"{FrameType} {CommandResponse}, n(r)={NR}, {pf}={p}",
            Ax25FrameType.UnknownU => "U other???",
            _ => $"{FrameType} {CommandResponse}, {pf}={p}",
        };
    }

    /// <summary>Info field as monitor text: printable bytes as-is (UTF-8 allowed), others as &lt;0xNN&gt; like Dire Wolf.</summary>
    public string InfoText => SafeText(Info);

    /// <summary>"SRC>DST,DIGI1*,DIGI2:info" for APRS style frames, with "(description)" before info for others.</summary>
    public string ToMonitorString()
    {
        var sb = new StringBuilder();
        sb.Append(Source.ToString(false)).Append('>').Append(Destination.ToString(false));
        foreach (var r in Repeaters) sb.Append(',').Append(r.ToString(true));
        sb.Append(':');
        if (!IsUiNoLayer3) sb.Append('(').Append(Describe()).Append(')');
        sb.Append(InfoText);
        return sb.ToString();
    }

    public static string SafeText(ReadOnlySpan<byte> data)
    {
        string s;
        try { s = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { s = Encoding.Latin1.GetString(data); }
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c < 0xA0)) sb.Append($"<0x{(int)c:x2}>");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Creates a UI frame (command, PID 0xF0 by default).</summary>
    public static Ax25Frame CreateUi(string source, string destination, IEnumerable<string>? path, string info, byte pid = 0xF0)
    {
        var dst = Ax25Address.Parse(destination) with { HBit = true };   // command: C bit set in destination
        var src = Ax25Address.Parse(source) with { HBit = false };
        var reps = (path ?? Array.Empty<string>()).Select(Ax25Address.Parse).ToArray();
        return new Ax25Frame(dst, src, reps, 0x03, pid, Encoding.UTF8.GetBytes(info));
    }

    public byte[] Encode()
    {
        bool hasPid = FrameType is Ax25FrameType.I or Ax25FrameType.UI;
        var buf = new byte[7 * (2 + Repeaters.Count) + 1 + (hasPid ? 1 : 0) + Info.Length];
        int o = 0;
        WriteAddress(buf, ref o, Destination, false);
        WriteAddress(buf, ref o, Source, Repeaters.Count == 0);
        for (int i = 0; i < Repeaters.Count; i++) WriteAddress(buf, ref o, Repeaters[i], i == Repeaters.Count - 1);
        buf[o++] = Control;
        if (hasPid) buf[o++] = Pid ?? 0xF0;
        Info.CopyTo(buf, o);
        return buf;
    }

    private static void WriteAddress(byte[] buf, ref int o, Ax25Address a, bool last)
    {
        string call = a.Callsign.ToUpperInvariant().PadRight(6);
        for (int i = 0; i < 6; i++) buf[o + i] = (byte)(call[i] << 1);
        buf[o + 6] = (byte)(0x60 | ((a.Ssid & 0x0F) << 1) | (a.HBit ? 0x80 : 0) | (last ? 1 : 0));
        o += 7;
    }

    /// <summary>Decodes an AX.25 frame without FCS (as delivered by KISS and AGW raw 'K').</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out Ax25Frame? frame, out string? error)
    {
        frame = null;
        error = null;
        if (data.Length < 15) { error = "Frame too short."; return false; }
        var addrs = new List<Ax25Address>();
        int o = 0;
        while (true)
        {
            if (o + 7 > data.Length) { error = "Address field not terminated."; return false; }
            var span = data.Slice(o, 7);
            var sb = new StringBuilder(6);
            for (int i = 0; i < 6; i++)
            {
                char c = (char)(span[i] >> 1);
                if ((span[i] & 1) != 0) { error = "Address extension bit set inside callsign."; return false; }
                if (c != ' ') sb.Append(c);
            }
            addrs.Add(new Ax25Address(sb.ToString(), (span[6] >> 1) & 0x0F, (span[6] & 0x80) != 0));
            o += 7;
            if ((span[6] & 1) != 0) break;
            if (addrs.Count >= 2 + MaxRepeaters) { error = "Too many addresses."; return false; }
        }
        if (addrs.Count < 2) { error = "Fewer than two addresses."; return false; }
        if (o >= data.Length) { error = "Missing control field."; return false; }
        byte control = data[o++];
        byte? pid = null;
        bool hasPid = (control & 1) == 0 || (control & 0xEF) == 0x03;
        if (hasPid)
        {
            if (o >= data.Length) { error = "Missing PID."; return false; }
            pid = data[o++];
        }
        frame = new Ax25Frame(addrs[0], addrs[1], addrs.Skip(2).ToArray(), control, pid, data[o..].ToArray());
        return true;
    }
}
