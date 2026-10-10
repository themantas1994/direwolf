using System.Globalization;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Packets;

namespace DireWolfGui.ViewModels;

public enum StationKindFilter { All, Stations, Objects }

/// <summary>Station list filter. Null members mean "any".</summary>
public sealed record StationFilterCriteria
{
    public string? Search { get; init; }
    public TimeSpan? MaxAge { get; init; }
    public double? MaxDistanceKm { get; init; }
    public int? Channel { get; init; }
    /// <summary>Only stations without a radio channel (heard via APRS-IS only).</summary>
    public bool NoChannelOnly { get; init; }
    public StationKindFilter Kind { get; init; }
    public bool HasPositionOnly { get; init; }
}

/// <summary>Pure helpers for the APRS page (filtering, distance text, freshness).</summary>
public static class StationFilter
{
    /// <summary>Positions older than this are drawn and listed as stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    public static bool Matches(TrackedStation s, StationFilterCriteria c, DateTimeOffset now, double? distanceKm)
    {
        if (c.HasPositionOnly && !s.HasPosition) return false;
        if (c.Kind == StationKindFilter.Stations && s.IsObject) return false;
        if (c.Kind == StationKindFilter.Objects && !s.IsObject) return false;
        if (c.MaxAge is TimeSpan age && now - s.LastHeard > age) return false;
        if (c.MaxDistanceKm is double max && (distanceKm is not double d || d > max)) return false;
        if (c.NoChannelOnly && s.Channel is not null) return false;
        if (c.Channel is int ch && s.Channel != ch) return false;
        if (!string.IsNullOrWhiteSpace(c.Search))
        {
            var q = c.Search.Trim();
            if (!Has(s.Callsign, q) && !Has(s.Comment, q) && !Has(s.Status, q) && !Has(s.ObjectOwner, q)) return false;
        }
        return true;
    }

    private static bool Has(string? text, string q) => text is not null && text.Contains(q, StringComparison.OrdinalIgnoreCase);

    public static double? DistanceKm(TrackedStation s, double? homeLat, double? homeLon) =>
        s.HasPosition && homeLat is double hl && homeLon is double ho
            ? GeoMath.DistanceKm(hl, ho, s.Latitude!.Value, s.Longitude!.Value)
            : null;

    /// <summary>"12.3 km NNE (23°)" or "" when either position is unknown.</summary>
    public static string DistanceText(TrackedStation s, double? homeLat, double? homeLon)
    {
        if (!s.HasPosition || homeLat is not double hl || homeLon is not double ho) return "";
        double km = GeoMath.DistanceKm(hl, ho, s.Latitude!.Value, s.Longitude!.Value);
        double brg = GeoMath.BearingDegrees(hl, ho, s.Latitude.Value, s.Longitude.Value);
        string dist = km < 10 ? km.ToString("0.0", CultureInfo.CurrentCulture) : km.ToString("0", CultureInfo.CurrentCulture);
        return $"{dist} km {GeoMath.CompassPoint(brg)} ({brg:0}°)";
    }

    public static bool IsPositionStale(TrackedStation s, DateTimeOffset now) =>
        s.PositionTime is { } t && now - t > StaleAfter;
}

/// <summary>One row of the station list.</summary>
public sealed class StationItemViewModel : Infrastructure.ObservableObject
{
    public StationItemViewModel(TrackedStation s) => Station = s;

    public string Id => Station.Callsign;

    private TrackedStation _station = null!;
    public TrackedStation Station
    {
        get => _station;
        private set => Set(ref _station, value);
    }

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => Set(ref _ageText, value); }

    private bool _isStale;
    /// <summary>Not heard for longer than <see cref="StationFilter.StaleAfter"/>.</summary>
    public bool IsStale { get => _isStale; private set => Set(ref _isStale, value); }

    private string _distanceText = "";
    public string DistanceText { get => _distanceText; private set => Set(ref _distanceText, value); }

    public double? DistanceKm { get; private set; }

    private string _positionText = "";
    public string PositionText { get => _positionText; private set => Set(ref _positionText, value); }

    private string _positionKind = "Neutral";
    /// <summary>Theme brush prefix: Success (fresh), Stale, Neutral (none).</summary>
    public string PositionKind { get => _positionKind; private set => Set(ref _positionKind, value); }

    public string Callsign => Station.Callsign;
    public string KindText => Station.IsObject ? (Station.ObjectOwner is { } o ? $"Object by {o}" : "Object") : "Station";
    public string Symbol => Station.Symbol ?? "";
    public string ChannelText => Station.Channel?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string OriginText => Station.Origin is PacketOrigin o ? PacketOriginText.Short(PacketDirection.Received, o) : "";
    public string OriginKind => Station.Origin is PacketOrigin o ? PacketOriginText.Kind(PacketDirection.Received, o) : "Neutral";
    public string CommentText => PacketOriginText.OneLine(string.Join("  ·  ", new[] { Station.Status, Station.Comment }.Where(x => !string.IsNullOrWhiteSpace(x))), 200);
    public long PacketCount => Station.PacketCount;
    public DateTimeOffset LastHeard => Station.LastHeard;

    /// <summary>Refreshes from a new snapshot (records are immutable, so a new reference means changed data).</summary>
    public void Update(TrackedStation s, DateTimeOffset now, double? homeLat, double? homeLon)
    {
        if (!ReferenceEquals(_station, s))
        {
            Station = s;
            foreach (var n in new[] { nameof(KindText), nameof(Symbol), nameof(ChannelText), nameof(OriginText), nameof(OriginKind),
                                      nameof(CommentText), nameof(PacketCount), nameof(LastHeard), nameof(Callsign) })
                OnPropertyChanged(n);
        }
        AgeText = Converters.AgeConverter.Format(now - s.LastHeard);
        IsStale = now - s.LastHeard > StationFilter.StaleAfter;
        DistanceKm = StationFilter.DistanceKm(s, homeLat, homeLon);
        DistanceText = StationFilter.DistanceText(s, homeLat, homeLon);
        if (!s.HasPosition)
        {
            PositionText = "no position";
            PositionKind = "Neutral";
        }
        else
        {
            var age = s.PositionTime is { } t ? Converters.AgeConverter.Format(now - t) + " ago" : "time unknown";
            bool stale = StationFilter.IsPositionStale(s, now);
            PositionText = $"{s.PositionSource ?? "?"}, {age}{(stale ? " (stale)" : "")}";
            PositionKind = stale ? "Stale" : "Success";
        }
    }
}

/// <summary>One label/value line of a detail pane.</summary>
public sealed record DetailRow(string Label, string Value);
