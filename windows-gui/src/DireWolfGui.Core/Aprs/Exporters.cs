using System.Globalization;
using System.Security;
using DireWolfGui.Core.Packets;

namespace DireWolfGui.Core.Aprs;

/// <summary>CSV exports of packets and stations, and GPX tracks built only from real track points.</summary>
public static class Exporters
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string CsvField(string? s)
    {
        s ??= "";
        // Neutralise spreadsheet formula injection, then quote if needed.
        if (s.Length > 0 && "=+-@".Contains(s[0]) && !double.TryParse(s, NumberStyles.Float, Inv, out _)) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string Num(double? v, string fmt = "0.######") => v?.ToString(fmt, Inv) ?? "";

    public static void WritePacketsCsv(IEnumerable<PacketRecord> packets, TextWriter w)
    {
        w.WriteLine("time_utc,direction,origin,channel,source,destination,path,audio_level,fec,type,latitude,longitude,info");
        foreach (var p in packets)
        {
            w.WriteLine(string.Join(",",
                p.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv), p.Direction, p.Origin, p.Channel?.ToString(Inv) ?? "",
                CsvField(p.Source), CsvField(p.Destination), CsvField(p.PathText), p.AudioLevel?.ToString(Inv) ?? "", CsvField(p.Fec),
                p.Aprs?.Type.ToString() ?? (p.FrameDescription != null ? "AX.25" : ""), Num(p.Aprs?.Latitude), Num(p.Aprs?.Longitude),
                CsvField(p.FrameDescription != null ? $"({p.FrameDescription}){p.Info}" : p.Info)));
        }
    }

    public static void WriteStationsCsv(IEnumerable<TrackedStation> stations, TextWriter w)
    {
        w.WriteLine("callsign,is_object,last_heard_utc,packets,channel,origin,latitude,longitude,altitude_ft,speed_kn,course,symbol,position_source,position_time_utc,audio_level,path,status,comment");
        foreach (var s in stations)
        {
            w.WriteLine(string.Join(",",
                CsvField(s.Callsign), s.IsObject ? "1" : "0", s.LastHeard.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv), s.PacketCount.ToString(Inv),
                s.Channel?.ToString(Inv) ?? "", s.Origin?.ToString() ?? "", Num(s.Latitude), Num(s.Longitude), Num(s.AltitudeFeet, "0.#"),
                Num(s.SpeedKnots, "0.#"), s.CourseDegrees?.ToString(Inv) ?? "", CsvField(s.Symbol), CsvField(s.PositionSource),
                s.PositionTime?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv) ?? "", s.AudioLevel?.ToString(Inv) ?? "",
                CsvField(s.Path), CsvField(s.Status), CsvField(s.Comment)));
        }
    }

    /// <summary>GPX 1.1 with one track per station that has at least one real track point.</summary>
    public static void WriteGpx(IEnumerable<TrackedStation> stations, TextWriter w, string creator = "Dire Wolf Station")
    {
        w.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        w.WriteLine($"<gpx version=\"1.1\" creator=\"{SecurityElement.Escape(creator)}\" xmlns=\"http://www.topografix.com/GPX/1/1\">");
        foreach (var s in stations.Where(s => s.Track.Count > 0))
        {
            w.WriteLine("  <trk>");
            w.WriteLine($"    <name>{SecurityElement.Escape(s.Callsign)}</name>");
            if (!string.IsNullOrEmpty(s.Comment)) w.WriteLine($"    <desc>{SecurityElement.Escape(s.Comment)}</desc>");
            w.WriteLine("    <trkseg>");
            foreach (var t in s.Track)
            {
                w.Write($"      <trkpt lat=\"{t.Latitude.ToString("0.######", Inv)}\" lon=\"{t.Longitude.ToString("0.######", Inv)}\">");
                if (t.AltitudeFeet is double alt) w.Write($"<ele>{(alt * 0.3048).ToString("0.#", Inv)}</ele>");
                w.Write($"<time>{t.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv)}</time>");
                w.WriteLine($"<src>{SecurityElement.Escape(t.Source)}</src></trkpt>");
            }
            w.WriteLine("    </trkseg>");
            w.WriteLine("  </trk>");
        }
        w.WriteLine("</gpx>");
    }
}
