using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Packets;

namespace DireWolfGui.ViewModels;

/// <summary>What the packet monitor shows. Null members mean "any".</summary>
public sealed record PacketFilterCriteria
{
    /// <summary>Free text matched (case-insensitive) against source, destination, path and info.</summary>
    public string? Search { get; init; }
    /// <summary>Callsign matched against source, destination, heard station and path. Without an SSID any SSID matches.</summary>
    public string? Callsign { get; init; }
    public int? Channel { get; init; }
    /// <summary>Only packets without a radio channel (e.g. "[ig]" APRS-IS lines).</summary>
    public bool NoChannelOnly { get; init; }
    public PacketDirection? Direction { get; init; }
    public PacketOrigin? Origin { get; init; }
    public AprsPacketType? AprsType { get; init; }
    /// <summary>Only frames that are not APRS (connected-mode / other AX.25 frames).</summary>
    public bool NonAprsOnly { get; init; }
    public bool WaprOnly { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Search) && string.IsNullOrWhiteSpace(Callsign) && Channel is null && !NoChannelOnly
        && Direction is null && Origin is null && AprsType is null && !NonAprsOnly && !WaprOnly;
}

/// <summary>Pure filter predicate for the packet monitor (applied to new batches and on a full re-filter).</summary>
public static class PacketFilter
{
    public static bool Matches(PacketRecord p, PacketFilterCriteria c)
    {
        if (c.WaprOnly && !p.IsWapr) return false;
        if (c.NoChannelOnly && p.Channel is not null) return false;
        if (c.Channel is int ch && p.Channel != ch) return false;
        if (c.Direction is PacketDirection d && p.Direction != d) return false;
        if (c.Origin is PacketOrigin o && p.Origin != o) return false;
        if (c.NonAprsOnly && p.Aprs is not null) return false;
        if (c.AprsType is AprsPacketType t && p.Aprs?.Type != t) return false;
        if (!string.IsNullOrWhiteSpace(c.Callsign) && !MatchesCallsign(p, c.Callsign.Trim())) return false;
        if (!string.IsNullOrWhiteSpace(c.Search))
        {
            var s = c.Search.Trim();
            if (!Contains(p.Source, s) && !Contains(p.Destination, s) && !Contains(p.PathText, s) && !Contains(p.Info, s)
                && !Contains(p.FrameDescription, s))
                return false;
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="call"/> names this station: "N0CALL" matches any SSID (N0CALL, N0CALL-9),
    /// "N0CALL-9" only that SSID. A trailing '*' (digipeated mark) is ignored.
    /// </summary>
    public static bool CallMatches(string? candidate, string call)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        candidate = candidate.TrimEnd('*');
        call = call.TrimEnd('*');
        if (call.Contains('-'))
            return string.Equals(NormalizeSsid(candidate), NormalizeSsid(call), StringComparison.OrdinalIgnoreCase);
        int dash = candidate.IndexOf('-');
        var baseCall = dash >= 0 ? candidate[..dash] : candidate;
        return string.Equals(baseCall, call, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSsid(string c) => c.EndsWith("-0", StringComparison.Ordinal) ? c[..^2] : c;

    private static bool MatchesCallsign(PacketRecord p, string call)
    {
        if (CallMatches(p.Source, call) || CallMatches(p.Destination, call) || CallMatches(p.Heard, call)) return true;
        foreach (var hop in p.Path) if (CallMatches(hop, call)) return true;
        if (p.Aprs?.Addressee is { } a && CallMatches(a.Trim(), call)) return true;
        if (p.Aprs?.ObjectName is { } n && CallMatches(n.Trim(), call)) return true;
        return false;
    }

    private static bool Contains(string? text, string s) =>
        text is not null && text.Contains(s, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Hex dump formatting for raw AX.25 bytes.</summary>
public static class HexDump
{
    /// <summary>"0000  82 a0 a4 ...  |ascii...|" lines; non-printable bytes shown as '.'.</summary>
    public static string Format(ReadOnlySpan<byte> data, int bytesPerLine = 16)
    {
        if (bytesPerLine < 1) throw new ArgumentOutOfRangeException(nameof(bytesPerLine));
        var sb = new StringBuilder();
        for (int off = 0; off < data.Length; off += bytesPerLine)
        {
            int n = Math.Min(bytesPerLine, data.Length - off);
            sb.Append(off.ToString("x4", CultureInfo.InvariantCulture)).Append("  ");
            for (int i = 0; i < bytesPerLine; i++)
            {
                if (i < n) sb.Append(data[off + i].ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < n; i++)
            {
                byte b = data[off + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.Append('|');
            if (off + bytesPerLine < data.Length) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>AX.25 addresses are ASCII shifted left one bit; this shows the address field unshifted.</summary>
    public static string DescribeAx25(byte[] raw)
    {
        if (!Ax25Frame.TryDecode(raw, out var f, out var err) || f is null)
            return "Could not decode the AX.25 frame: " + (err ?? "unknown error");
        var sb = new StringBuilder();
        sb.Append("Destination : ").Append(f.Destination.ToString()).Append(f.Destination.HBit ? "  (C bit set)" : "").Append('\n');
        sb.Append("Source      : ").Append(f.Source.ToString()).Append(f.Source.HBit ? "  (C bit set)" : "").Append('\n');
        for (int i = 0; i < f.Repeaters.Count; i++)
            sb.Append("Digi ").Append(i + 1).Append("      : ").Append(f.Repeaters[i].ToString(true))
              .Append(f.Repeaters[i].HBit ? "  (has been repeated)" : "").Append('\n');
        sb.Append("Control     : 0x").Append(f.Control.ToString("x2", CultureInfo.InvariantCulture)).Append("  ").Append(f.FrameType)
          .Append(" — ").Append(f.Describe()).Append('\n');
        if (f.Pid is byte pid) sb.Append("PID         : 0x").Append(pid.ToString("x2", CultureInfo.InvariantCulture))
          .Append(pid == 0xF0 ? "  (no layer 3)" : "").Append('\n');
        sb.Append("Info        : ").Append(f.Info.Length).Append(" bytes\n");
        sb.Append("Monitor     : ").Append(f.ToMonitorString());
        return sb.ToString();
    }
}

/// <summary>Clipboard access that never throws (the clipboard can be locked by another application).</summary>
public static class ClipboardHelper
{
    public static bool TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(30);
            }
        }
        return false;
    }
}

/// <summary>
/// ObservableCollection with batch operations for live views: small batches raise normal per-item
/// notifications, large ones a single Reset, so a busy channel never floods the UI with events.
/// Must only be used on the UI thread.
/// </summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Batches larger than this raise one Reset instead of per-item notifications.</summary>
    public int ResetThreshold { get; init; } = 200;

    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;
        if (items.Count <= ResetThreshold)
        {
            foreach (var i in items) Add(i);
            return;
        }
        CheckReentrancy();
        foreach (var i in items) Items.Add(i);
        RaiseReset();
    }

    /// <summary>Removes the oldest <paramref name="count"/> items.</summary>
    public void RemoveFirst(int count)
    {
        count = Math.Min(count, Count);
        if (count <= 0) return;
        if (count <= 20)
        {
            for (int i = 0; i < count; i++) RemoveAt(0);
            return;
        }
        CheckReentrancy();
        if (Items is List<T> list) list.RemoveRange(0, count);
        else for (int i = 0; i < count; i++) Items.RemoveAt(0);
        RaiseReset();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>A labelled choice for a filter ComboBox.</summary>
public sealed record FilterChoice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
