namespace DireWolfGui.Core.Aprs;

public enum AprsPacketType
{
    Position, PositionWithTimestamp, MicE, Object, Item, Message, MessageAck, MessageReject, Bulletin,
    Status, Weather, Telemetry, Query, ThirdParty, Other,
}

/// <summary>Result of <see cref="AprsParser.Parse"/>. Position fields are null unless really present in the packet.</summary>
public sealed class AprsInfo
{
    public AprsPacketType Type { get; init; }
    /// <summary>First character of the info field (APRS data type identifier), '\0' if empty.</summary>
    public char DataType { get; init; }

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    /// <summary>Number of digits blanked for position ambiguity (0 = exact).</summary>
    public int PositionAmbiguity { get; init; }
    public bool Compressed { get; init; }
    public char? SymbolTable { get; init; }
    public char? SymbolCode { get; init; }
    public int? CourseDegrees { get; init; }
    public double? SpeedKnots { get; init; }
    public double? AltitudeFeet { get; init; }
    /// <summary>Timestamp text as sent (e.g. "092345z"), not interpreted.</summary>
    public string? Timestamp { get; init; }
    public string? Comment { get; init; }

    public string? ObjectName { get; init; }
    /// <summary>Object/item alive (true) or killed (false).</summary>
    public bool? ObjectAlive { get; init; }

    public string? Addressee { get; init; }
    public string? MessageText { get; init; }
    /// <summary>Message number (for messages) or the number being acknowledged/rejected.</summary>
    public string? MessageId { get; init; }
    /// <summary>Reply-ack: id of an earlier message of ours acknowledged inside this message ("{MM}AA").</summary>
    public string? ReplyAck { get; init; }

    public string? StatusText { get; init; }
    /// <summary>Mic-E status, e.g. "En Route", "Emergency".</summary>
    public string? MicEMessage { get; init; }
    /// <summary>Third-party payload ("SRC>DST,PATH:info") for '}' packets.</summary>
    public string? ThirdPartyPayload { get; init; }
    /// <summary>Why a position could not be decoded, if it looked like it should have one.</summary>
    public string? Error { get; init; }

    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;
    public string? Symbol => SymbolTable.HasValue && SymbolCode.HasValue ? $"{SymbolTable}{SymbolCode}" : null;
}
