using DireWolfGui.Core.Packets;

namespace DireWolfGui.Core.Aprs;

public static class PositionSources
{
    public const string Packet = "packet";
    public const string DireWolfLog = "Dire Wolf log";
}

public sealed record TrackPoint(DateTimeOffset Time, double Latitude, double Longitude, double? AltitudeFeet, double? SpeedKnots, int? CourseDegrees, string Source);

/// <summary>Snapshot of what is known about one station (or APRS object/item).</summary>
public sealed record TrackedStation
{
    public required string Callsign { get; init; }
    public bool IsObject { get; init; }
    /// <summary>For objects/items: the station that sent it.</summary>
    public string? ObjectOwner { get; init; }
    public DateTimeOffset FirstHeard { get; init; }
    public DateTimeOffset LastHeard { get; init; }
    public long PacketCount { get; init; }
    public int? Channel { get; init; }
    public PacketOrigin? Origin { get; init; }
    public string? Path { get; init; }
    public int? AudioLevel { get; init; }
    public string? Status { get; init; }
    public string? Comment { get; init; }
    public string? Symbol { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? AltitudeFeet { get; init; }
    public double? SpeedKnots { get; init; }
    public int? CourseDegrees { get; init; }
    /// <summary>Where the position came from: <see cref="PositionSources.Packet"/> or <see cref="PositionSources.DireWolfLog"/>.</summary>
    public string? PositionSource { get; init; }
    public DateTimeOffset? PositionTime { get; init; }
    public IReadOnlyList<TrackPoint> Track { get; init; } = Array.Empty<TrackPoint>();

    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;
}

/// <summary>
/// Stations heard, with positions only from real decoded data (our APRS parse of received packets or
/// Dire Wolf's CSV log). Bounded: the least recently heard stations and oldest track points are dropped.
/// Thread-safe; readers get immutable snapshots.
/// </summary>
public sealed class StationTracker
{
    private readonly Dictionary<string, TrackedStation> _stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private long _version;

    public int MaxStations { get; init; } = 2000;
    public int MaxTrackPoints { get; init; } = 500;

    /// <summary>Incremented on every change; UIs can poll it cheaply.</summary>
    public long Version => Interlocked.Read(ref _version);
    public int Count { get { lock (_lock) return _stations.Count; } }

    public event Action<TrackedStation>? StationUpdated;

    /// <summary>Updates from a packet. Only received packets count (our own transmissions are ignored).</summary>
    public TrackedStation? Update(PacketRecord p)
    {
        if (p.Direction != PacketDirection.Received || string.IsNullOrEmpty(p.Source)) return null;
        var a = p.Aprs;
        bool isObj = a is { Type: AprsPacketType.Object or AprsPacketType.Item } && !string.IsNullOrEmpty(a.ObjectName);
        string key = isObj ? a!.ObjectName! : p.Source;
        TrackedStation result;
        lock (_lock)
        {
            var s = Get(key, p.Time, isObj);
            s = s with
            {
                LastHeard = Max(s.LastHeard, p.Time), PacketCount = s.PacketCount + 1, Channel = p.Channel ?? s.Channel, Origin = p.Origin,
                Path = p.PathText, AudioLevel = p.AudioLevel ?? s.AudioLevel, ObjectOwner = isObj ? p.Source : s.ObjectOwner,
            };
            if (a != null)
            {
                if (a.Type == AprsPacketType.Status) s = s with { Status = a.StatusText };
                if (a.MicEMessage != null) s = s with { Status = a.MicEMessage };
                if (a.Comment != null && a.Type is not (AprsPacketType.Message or AprsPacketType.Telemetry or AprsPacketType.Other or AprsPacketType.Query))
                    s = s with { Comment = a.Comment };
                if (a.HasPosition)
                    s = WithPosition(s, p.Time, a.Latitude!.Value, a.Longitude!.Value, a.AltitudeFeet, a.SpeedKnots, a.CourseDegrees, a.Symbol, PositionSources.Packet);
            }
            result = Store(key, s);
        }
        StationUpdated?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Updates from a Dire Wolf CSV log line. <paramref name="countAsPacket"/> should be false when the same
    /// packets are also fed through <see cref="Update(PacketRecord)"/> (live station), true when replaying a log.
    /// </summary>
    public TrackedStation? Update(DireWolfLogRecord r, bool countAsPacket)
    {
        string key = !string.IsNullOrEmpty(r.Name) ? r.Name : r.Source;
        if (string.IsNullOrEmpty(key)) return null;
        bool isObj = !string.Equals(key, r.Source, StringComparison.OrdinalIgnoreCase);
        TrackedStation result;
        lock (_lock)
        {
            var s = Get(key, r.Time, isObj);
            s = s with
            {
                LastHeard = Max(s.LastHeard, r.Time), PacketCount = s.PacketCount + (countAsPacket ? 1 : 0),
                Channel = r.IsOwnBeacon ? s.Channel : r.Channel, AudioLevel = r.AudioLevel ?? s.AudioLevel,
                ObjectOwner = isObj ? r.Source : s.ObjectOwner,
                Status = r.Status.Length > 0 ? r.Status : s.Status,
                Comment = r.Comment.Length > 0 ? r.Comment : s.Comment,
                Origin = s.Origin ?? (r.IsOwnBeacon ? PacketOrigin.Local : PacketOrigin.Radio),
            };
            if (r.HasPosition)
                s = WithPosition(s, r.Time, r.Latitude!.Value, r.Longitude!.Value, r.AltitudeMeters * 3.28084, r.SpeedKnots,
                    r.Course is double c ? (int)Math.Round(c) : null, r.Symbol.Length == 2 ? r.Symbol : s.Symbol, PositionSources.DireWolfLog);
            result = Store(key, s);
        }
        StationUpdated?.Invoke(result);
        return result;
    }

    private TrackedStation Get(string key, DateTimeOffset time, bool isObj) =>
        _stations.TryGetValue(key, out var s) ? s : new TrackedStation { Callsign = key, IsObject = isObj, FirstHeard = time, LastHeard = time };

    private TrackedStation WithPosition(TrackedStation s, DateTimeOffset time, double lat, double lon, double? alt, double? speed, int? course, string? symbol, string source)
    {
        var track = s.Track;
        var last = track.Count > 0 ? track[^1] : null;
        if (last == null || last.Latitude != lat || last.Longitude != lon)
        {
            var list = new List<TrackPoint>(track) { new(time, lat, lon, alt, speed, course, source) };
            if (list.Count > MaxTrackPoints) list.RemoveRange(0, list.Count - MaxTrackPoints);
            track = list;
        }
        return s with
        {
            Latitude = lat, Longitude = lon, AltitudeFeet = alt ?? s.AltitudeFeet, SpeedKnots = speed, CourseDegrees = course,
            Symbol = symbol ?? s.Symbol, PositionSource = source, PositionTime = time, Track = track,
        };
    }

    private TrackedStation Store(string key, TrackedStation s)
    {
        _stations[key] = s;
        if (_stations.Count > MaxStations)
        {
            var oldest = _stations.Values.OrderBy(x => x.LastHeard).Take(_stations.Count - MaxStations).Select(x => x.Callsign).ToList();
            foreach (var k in oldest) _stations.Remove(k);
        }
        Interlocked.Increment(ref _version);
        return s;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    public IReadOnlyList<TrackedStation> GetStations()
    {
        lock (_lock) return _stations.Values.OrderByDescending(s => s.LastHeard).ToArray();
    }

    public TrackedStation? TryGet(string callsign)
    {
        lock (_lock) return _stations.TryGetValue(callsign, out var s) ? s : null;
    }

    public void Clear()
    {
        lock (_lock) _stations.Clear();
        Interlocked.Increment(ref _version);
    }
}

public static class GeoMath
{
    private const double EarthRadiusKm = 6371.0088;

    /// <summary>Great circle distance in kilometres (haversine).</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = Rad(lat2 - lat1), dLon = Rad(lon2 - lon1);
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Initial bearing from point 1 to point 2, degrees 0-360 (0 = north).</summary>
    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        double y = Math.Sin(Rad(lon2 - lon1)) * Math.Cos(Rad(lat2));
        double x = Math.Cos(Rad(lat1)) * Math.Sin(Rad(lat2)) - Math.Sin(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Cos(Rad(lon2 - lon1));
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    public static double KmToMiles(double km) => km / 1.609344;

    /// <summary>16-point compass name for a bearing, e.g. "NNE".</summary>
    public static string CompassPoint(double bearing)
    {
        string[] names = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
        return names[(int)Math.Round(((bearing % 360) + 360) % 360 / 22.5) % 16];
    }

    private static double Rad(double d) => d * Math.PI / 180;
}
