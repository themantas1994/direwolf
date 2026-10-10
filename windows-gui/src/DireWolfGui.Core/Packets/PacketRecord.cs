using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Logging;

namespace DireWolfGui.Core.Packets;

public enum PacketDirection { Received, Transmitted }

public enum PacketOrigin { Radio, Local, FromAprsIs, ToAprsIs, IgateToRadio, Gateway, NetworkTnc, Dtmf, Ais, Unknown }

public enum PacketSourceKind { Console, AgwMonitor, Kiss }

/// <summary>A packet seen by the station (received or transmitted), as reported by Dire Wolf.</summary>
public sealed class PacketRecord : ISequenced
{
    private IReadOnlyList<string> _decodedLines = Array.Empty<string>();

    public long Sequence { get; set; }
    public DateTimeOffset Time { get; init; }
    /// <summary>Radio/virtual channel, or null when not known (e.g. "[ig]").</summary>
    public int? Channel { get; init; }
    /// <summary>The bracketed label as printed by Dire Wolf without brackets, e.g. "0.1", "0L", "ig>tx".</summary>
    public string Label { get; init; } = "";
    public PacketDirection Direction { get; init; }
    public PacketOrigin Origin { get; init; }
    public PacketSourceKind SourceKind { get; init; }
    public string Source { get; init; } = "";
    public string Destination { get; init; } = "";
    /// <summary>Digipeater path as printed, used ones keep their trailing '*'.</summary>
    public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
    /// <summary>Info field with Dire Wolf's &lt;0xNN&gt; escapes turned back into characters.</summary>
    public string Info { get; init; } = "";
    /// <summary>For non-APRS frames, Dire Wolf's description, e.g. "SABM cmd, p=1".</summary>
    public string? FrameDescription { get; init; }
    /// <summary>Station actually heard (may be a digipeater), from the audio level line.</summary>
    public string? Heard { get; init; }
    public int? AudioLevel { get; init; }
    /// <summary>Full audio level text, e.g. "50(14/14)".</summary>
    public string? AudioLevelText { get; init; }
    /// <summary>"FX.25", "IL2P", "WAPR" or a retry/fix-bits indication such as "[NONE]".</summary>
    public string? Fec { get; init; }
    public bool IsWapr { get; init; }
    /// <summary>WAPR receive details such as "SNR 12 dB, +3 Hz".</summary>
    public string? WaprDetails { get; init; }
    /// <summary>The line exactly as printed by Dire Wolf.</summary>
    public string RawText { get; init; } = "";
    /// <summary>Our own APRS parse of <see cref="Info"/> (null if not an APRS-looking frame).</summary>
    public AprsInfo? Aprs { get; init; }

    /// <summary>Raw AX.25 bytes (no FCS) when known, e.g. from the AGW raw feed. Set at most once.</summary>
    public byte[]? RawFrame { get; set; }

    /// <summary>Dire Wolf's own human readable decode lines that followed the packet line. Grows while they arrive.</summary>
    public IReadOnlyList<string> DecodedLines => _decodedLines;

    internal void AppendDecodedLine(string line)
    {
        var copy = new List<string>(_decodedLines) { line };
        _decodedLines = copy;   // reference swap: readers always see a complete list
    }

    public string PathText => string.Join(",", Path);

    /// <summary>"SRC>DST,PATH:info" form.</summary>
    public string ToMonitorString() =>
        $"{Source}>{Destination}{(Path.Count > 0 ? "," + PathText : "")}:{(FrameDescription != null ? "(" + FrameDescription + ")" : "")}{Info}";

    public override string ToString() => $"[{Label}] {ToMonitorString()}";
}
