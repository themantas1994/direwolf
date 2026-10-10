using System.Globalization;
using System.Text;

namespace DireWolfGui.Core.Aprs;

/// <summary>One line of Dire Wolf's CSV log (-l dir / -L file), see src/log.c.</summary>
public sealed record DireWolfLogRecord
{
    /// <summary>Radio channel; 999 means our own tracker beacon.</summary>
    public int Channel { get; init; }
    public DateTimeOffset Time { get; init; }
    public string IsoTime { get; init; } = "";
    public string Source { get; init; } = "";
    public string Heard { get; init; } = "";
    public string Level { get; init; } = "";
    public string Error { get; init; } = "";
    public string DataType { get; init; } = "";
    public string Name { get; init; } = "";
    public string Symbol { get; init; } = "";
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? SpeedKnots { get; init; }
    public double? Course { get; init; }
    public double? AltitudeMeters { get; init; }
    public string Frequency { get; init; } = "";
    public string Offset { get; init; } = "";
    public string Tone { get; init; } = "";
    public string System { get; init; } = "";
    public string Status { get; init; } = "";
    public string Telemetry { get; init; } = "";
    public string Comment { get; init; } = "";

    public bool IsOwnBeacon => Channel == 999;
    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;
    /// <summary>Audio level number from "50(14/14)".</summary>
    public int? AudioLevel => int.TryParse(Level.Split('(')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
}

public static class DireWolfCsvLog
{
    public const string Header = "chan,utime,isotime,source,heard,level,error,dti,name,symbol,latitude,longitude,speed,course,altitude,frequency,offset,tone,system,status,telemetry,comment";

    /// <summary>Splits one CSV line using Dire Wolf's quoting (fields with , or " are quoted, " doubled).</summary>
    public static IReadOnlyList<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"' && sb.Length == 0) quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>Parses a data line. Returns false for the header, blank or malformed lines.</summary>
    public static bool TryParse(string line, out DireWolfLogRecord? record)
    {
        record = null;
        line = line.TrimEnd('\r', '\n');
        if (line.Length == 0 || line.StartsWith("chan,", StringComparison.Ordinal)) return false;
        var f = SplitLine(line);
        if (f.Count < 22) return false;
        if (!int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int chan)) return false;
        if (!long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long utime)) return false;
        // The comment is the last field; if it contained an unquoted comma, keep the rest.
        string comment = f.Count == 22 ? f[21] : string.Join(",", f.Skip(21));
        record = new DireWolfLogRecord
        {
            Channel = chan, Time = DateTimeOffset.FromUnixTimeSeconds(utime), IsoTime = f[2], Source = f[3], Heard = f[4], Level = f[5],
            Error = f[6], DataType = f[7], Name = f[8], Symbol = f[9], Latitude = D(f[10]), Longitude = D(f[11]), SpeedKnots = D(f[12]),
            Course = D(f[13]), AltitudeMeters = D(f[14]), Frequency = f[15], Offset = f[16], Tone = f[17], System = f[18], Status = f[19],
            Telemetry = f[20], Comment = comment,
        };
        if (record.Latitude is < -90 or > 90 || record.Longitude is < -180 or > 180)
            record = record with { Latitude = null, Longitude = null };
        return true;
    }

    private static double? D(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary>Daily log file name Dire Wolf uses for a UTC date.</summary>
    public static string DailyFileName(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";
}

/// <summary>
/// Follows Dire Wolf's CSV log while it is written. Directory mode follows the current UTC daily file
/// (YYYY-MM-DD.log) across midnight; single-file mode follows one file (-L). Handles truncation and
/// partially written lines. Call <see cref="Poll"/> yourself or <see cref="Start"/> a timer.
/// </summary>
public sealed class DireWolfCsvLogTailer : IDisposable
{
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private ITimer? _timer;
    private string? _currentFile;
    private long _offset;
    private bool _firstFile = true;

    /// <param name="path">Log directory (daily files) or a single log file when <paramref name="singleFile"/>.</param>
    /// <param name="startAtEnd">True: only report lines written from now on. False: load what is already there.</param>
    public DireWolfCsvLogTailer(string path, bool singleFile = false, bool startAtEnd = true, TimeProvider? timeProvider = null)
    {
        Path = path;
        SingleFile = singleFile;
        StartAtEnd = startAtEnd;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Path { get; }
    public bool SingleFile { get; }
    public bool StartAtEnd { get; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public string? CurrentFile { get { lock (_lock) return _currentFile; } }

    public event Action<DireWolfLogRecord>? RecordRead;

    public void Start() => _timer ??= _time.CreateTimer(_ => { try { Poll(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }, null, TimeSpan.Zero, PollInterval);

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Reads anything new. Returns the records found (also raised through <see cref="RecordRead"/>).</summary>
    public IReadOnlyList<DireWolfLogRecord> Poll()
    {
        var found = new List<DireWolfLogRecord>();
        lock (_lock)
        {
            string target = SingleFile ? Path : System.IO.Path.Combine(Path, DireWolfCsvLog.DailyFileName(_time.GetUtcNow()));
            if (_currentFile != null && !string.Equals(_currentFile, target, StringComparison.Ordinal))
            {
                ReadNew(_currentFile, found);   // finish yesterday's file before switching
                _currentFile = null;
            }
            if (_currentFile == null)
            {
                if (!File.Exists(target))
                {
                    _firstFile = false;   // "start at end" only skips content that existed when tailing began
                    return found;
                }
                _currentFile = target;
                _offset = _firstFile && StartAtEnd ? new FileInfo(target).Length : 0;
                _firstFile = false;
            }
            ReadNew(_currentFile, found);
        }
        foreach (var r in found) RecordRead?.Invoke(r);
        return found;
    }

    private void ReadNew(string file, List<DireWolfLogRecord> found)
    {
        if (!File.Exists(file)) return;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset) { _offset = 0; }   // truncated or replaced
        if (fs.Length == _offset) return;
        fs.Seek(_offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - _offset];
        int n = 0, r;
        while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
        // Only consume complete lines; keep the byte offset of the last newline.
        int lastNl = n == 0 ? -1 : Array.LastIndexOf(buf, (byte)'\n', n - 1);
        if (lastNl < 0) return;   // partial line: wait for the rest
        _offset += lastNl + 1;
        string text = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
        foreach (var line in text.Split('\n'))
            if (DireWolfCsvLog.TryParse(line, out var rec)) found.Add(rec!);
    }

    public void Dispose() => Stop();
}
