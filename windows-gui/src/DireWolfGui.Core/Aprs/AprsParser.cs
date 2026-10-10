using System.Globalization;
using System.Text.RegularExpressions;

namespace DireWolfGui.Core.Aprs;

/// <summary>
/// Parser for the APRS info field (APRS 1.0.1 plus common extensions). Only decodes what is really in the
/// packet: when a position is malformed, latitude/longitude stay null and <see cref="AprsInfo.Error"/> says why.
/// </summary>
public static partial class AprsParser
{
    [GeneratedRegex(@"/A=(-?\d{6})")]
    private static partial Regex AltitudeExt();

    [GeneratedRegex(@"^(\d{3})/(\d{3})")]
    private static partial Regex CourseSpeed();

    [GeneratedRegex(@"^[`'>\]]?([!-{]{3})\}")]
    private static partial Regex MicEAltitude();

    [GeneratedRegex(@"^[A-Za-z0-9]{1,5}$")]
    private static partial Regex MessageIdPattern();

    public static AprsInfo Parse(string destination, string info)
    {
        destination ??= "";
        info ??= "";
        if (info.Length == 0) return new AprsInfo { Type = AprsPacketType.Other };
        char dti = info[0];
        try
        {
            switch (dti)
            {
                case '!':
                case '=':
                    return ParsePositionAt(info, 1, AprsPacketType.Position, dti, null);
                case '/':
                case '@':
                    if (info.Length < 8) return Other(dti, "Timestamp too short.");
                    return ParsePositionAt(info, 8, AprsPacketType.PositionWithTimestamp, dti, info.Substring(1, 7));
                case ';':
                    return ParseObject(info);
                case ')':
                    return ParseItem(info);
                case ':':
                    return ParseMessage(info);
                case '>':
                    return new AprsInfo { Type = AprsPacketType.Status, DataType = dti, StatusText = info[1..].TrimEnd('\r', '\n') };
                case '`':
                case '\'':
                case (char)0x1c:
                case (char)0x1d:
                    return ParseMicE(destination, info);
                case '_':
                    return new AprsInfo { Type = AprsPacketType.Weather, DataType = dti, Timestamp = info.Length >= 9 ? info.Substring(1, 8) : null, Comment = info[1..] };
                case 'T' when info.StartsWith("T#", StringComparison.Ordinal):
                    return new AprsInfo { Type = AprsPacketType.Telemetry, DataType = dti, Comment = info[2..] };
                case '?':
                    return new AprsInfo { Type = AprsPacketType.Query, DataType = dti, Comment = info[1..] };
                case '}':
                    return new AprsInfo { Type = AprsPacketType.ThirdParty, DataType = dti, ThirdPartyPayload = info[1..] };
            }
            // APRS allows a '!' position anywhere in the first 40 characters (e.g. some TNC beacons).
            int bang = info.IndexOf('!', 0, Math.Min(40, info.Length));
            if (bang > 0)
            {
                var p = ParsePositionAt(info, bang + 1, AprsPacketType.Position, dti, null);
                if (p.HasPosition) return p;
            }
            return Other(dti, null);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return Other(dti, "Malformed: " + ex.Message);
        }
    }

    private static AprsInfo Other(char dti, string? error) => new() { Type = AprsPacketType.Other, DataType = dti, Error = error };

    private sealed class Pos
    {
        public double? Lat, Lon;
        public int Ambiguity;
        public bool Compressed;
        public char? Table, Code;
        public int? Course;
        public double? Speed, Alt;
        public string? Comment, Error;
    }

    private static AprsInfo ParsePositionAt(string info, int start, AprsPacketType type, char dti, string? ts)
    {
        var p = DecodePosition(info, start);
        if (p.Code == '_' && p.Error == null) type = AprsPacketType.Weather;
        return new AprsInfo
        {
            Type = type, DataType = dti, Timestamp = ts, Latitude = p.Lat, Longitude = p.Lon, PositionAmbiguity = p.Ambiguity,
            Compressed = p.Compressed, SymbolTable = p.Table, SymbolCode = p.Code, CourseDegrees = p.Course, SpeedKnots = p.Speed,
            AltitudeFeet = p.Alt, Comment = p.Comment, Error = p.Error,
        };
    }

    private static Pos DecodePosition(string info, int start)
    {
        if (start >= info.Length) return new Pos { Error = "Position missing." };
        char first = info[start];
        return char.IsAsciiDigit(first) || first == ' ' ? DecodeUncompressed(info, start) : DecodeCompressed(info, start);
    }

    private static Pos DecodeUncompressed(string info, int start)
    {
        if (info.Length < start + 19) return new Pos { Error = "Position too short." };
        string lat = info.Substring(start, 8), lon = info.Substring(start + 9, 9);
        char table = info[start + 8], code = info[start + 18];
        int amb = lat.Take(7).Count(c => c == ' ');
        double? la = ParseDegMin(lat.Replace(' ', '0'), 2, 'N', 'S', 90);
        double? lo = ParseDegMin(lon.Replace(' ', '0'), 3, 'E', 'W', 180);
        if (la == null || lo == null) return new Pos { Error = "Invalid latitude/longitude." , Table = table, Code = code };
        var p = new Pos { Lat = la, Lon = lo, Ambiguity = amb, Table = table, Code = code };
        ParseExtensions(p, info[(start + 19)..]);
        return p;
    }

    private static double? ParseDegMin(string s, int degDigits, char pos, char neg, int maxDeg)
    {
        // DDMM.hhN / DDDMM.hhW
        if (s.Length != degDigits + 6 || s[degDigits + 2] != '.') return null;
        char hemi = char.ToUpperInvariant(s[^1]);
        if (hemi != pos && hemi != neg) return null;
        if (!int.TryParse(s.AsSpan(0, degDigits), NumberStyles.None, CultureInfo.InvariantCulture, out int deg)) return null;
        if (!double.TryParse(s.AsSpan(degDigits, 5), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double min)) return null;
        if (deg > maxDeg || min >= 60 || (deg == maxDeg && min > 0)) return null;
        double v = deg + min / 60.0;
        return hemi == neg ? -v : v;
    }

    private static Pos DecodeCompressed(string info, int start)
    {
        if (info.Length < start + 13) return new Pos { Error = "Compressed position too short." };
        char table = info[start];
        if (!(table == '/' || table == '\\' || char.IsAsciiLetterUpper(table) || (table >= 'a' && table <= 'j')))
            return new Pos { Error = "Invalid symbol table for compressed position." };
        string s = info.Substring(start, 13);
        for (int i = 1; i <= 8; i++) if (s[i] < '!' || s[i] > '{') return new Pos { Error = "Invalid compressed position characters." };
        double y = Base91(s.AsSpan(1, 4)), x = Base91(s.AsSpan(5, 4));
        double lat = 90 - y / 380926.0, lon = -180 + x / 190463.0;
        if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return new Pos { Error = "Compressed position out of range." };
        // Overlay a-j means digit 0-9 overlay on the alternate table.
        char tableOut = table >= 'a' && table <= 'j' ? (char)('0' + (table - 'a')) : table;
        var p = new Pos { Lat = lat, Lon = lon, Compressed = true, Table = tableOut, Code = s[9] };
        char c = s[10], sp = s[11];
        if (c != ' ' && c >= '!' && c <= 'z' && sp >= '!' && sp <= '{')
        {
            int t = s[12] - 33;
            if (((t >> 3) & 3) == 2) p.Alt = Math.Pow(1.002, (c - 33) * 91 + (sp - 33));
            else { p.Course = (c - 33) * 4; p.Speed = Math.Pow(1.08, sp - 33) - 1; }
        }
        ParseExtensions(p, info[(start + 13)..], allowCourseSpeed: false);
        return p;
    }

    private static void ParseExtensions(Pos p, string rest, bool allowCourseSpeed = true)
    {
        if (allowCourseSpeed)
        {
            var cs = CourseSpeed().Match(rest);
            if (cs.Success)
            {
                int course = int.Parse(cs.Groups[1].Value, CultureInfo.InvariantCulture);
                int speed = int.Parse(cs.Groups[2].Value, CultureInfo.InvariantCulture);
                if (course >= 1 && course <= 360) p.Course = course;
                if (course != 0 || speed != 0) p.Speed = speed;
                rest = rest[7..];
            }
        }
        var alt = AltitudeExt().Match(rest);
        if (alt.Success)
        {
            p.Alt = int.Parse(alt.Groups[1].Value, CultureInfo.InvariantCulture);
            rest = rest.Remove(alt.Index, alt.Length);
        }
        rest = rest.TrimEnd('\r', '\n');
        p.Comment = rest.Length > 0 ? rest : null;
    }

    private static double Base91(ReadOnlySpan<char> s)
    {
        double v = 0;
        foreach (char c in s) v = v * 91 + (c - 33);
        return v;
    }

    private static AprsInfo ParseObject(string info)
    {
        if (info.Length < 18) return Other(';', "Object too short.");
        string name = info.Substring(1, 9).TrimEnd();
        char state = info[10];
        if (state != '*' && state != '_') return Other(';', "Object without live/killed flag.");
        var p = DecodePosition(info, 18);
        return new AprsInfo
        {
            Type = AprsPacketType.Object, DataType = ';', ObjectName = name, ObjectAlive = state == '*', Timestamp = info.Substring(11, 7),
            Latitude = p.Lat, Longitude = p.Lon, PositionAmbiguity = p.Ambiguity, Compressed = p.Compressed, SymbolTable = p.Table,
            SymbolCode = p.Code, CourseDegrees = p.Course, SpeedKnots = p.Speed, AltitudeFeet = p.Alt, Comment = p.Comment, Error = p.Error,
        };
    }

    private static AprsInfo ParseItem(string info)
    {
        int end = info.IndexOfAny(new[] { '!', '_' }, 1);
        if (end < 4 || end > 10) return Other(')', "Item name must be 3-9 characters.");
        string name = info[1..end];
        var p = DecodePosition(info, end + 1);
        return new AprsInfo
        {
            Type = AprsPacketType.Item, DataType = ')', ObjectName = name, ObjectAlive = info[end] == '!',
            Latitude = p.Lat, Longitude = p.Lon, PositionAmbiguity = p.Ambiguity, Compressed = p.Compressed, SymbolTable = p.Table,
            SymbolCode = p.Code, CourseDegrees = p.Course, SpeedKnots = p.Speed, AltitudeFeet = p.Alt, Comment = p.Comment, Error = p.Error,
        };
    }

    private static AprsInfo ParseMessage(string info)
    {
        if (info.Length < 11 || info[10] != ':') return Other(':', "Malformed message (addressee must be 9 characters).");
        string addressee = info.Substring(1, 9).Trim();
        string text = info[11..].TrimEnd('\r', '\n');

        if (text.StartsWith("ack", StringComparison.Ordinal) || text.StartsWith("rej", StringComparison.Ordinal))
        {
            string id = text[3..].Trim();
            int brace = id.IndexOf('}');
            if (brace >= 0) id = id[..brace];   // reply-ack style "ackMM}" acknowledges MM
            if (MessageIdPattern().IsMatch(id))
                return new AprsInfo
                {
                    Type = text[0] == 'a' ? AprsPacketType.MessageAck : AprsPacketType.MessageReject,
                    DataType = ':', Addressee = addressee, MessageId = id, MessageText = text,
                };
        }

        if (addressee.StartsWith("BLN", StringComparison.OrdinalIgnoreCase) || addressee.StartsWith("NWS", StringComparison.OrdinalIgnoreCase))
            return new AprsInfo { Type = AprsPacketType.Bulletin, DataType = ':', Addressee = addressee, MessageText = text };

        string? msgId = null, replyAck = null;
        int open = text.LastIndexOf('{');
        if (open >= 0)
        {
            string tail = text[(open + 1)..].Trim();
            int close = tail.IndexOf('}');
            string mm = close >= 0 ? tail[..close] : tail;
            string aa = close >= 0 ? tail[(close + 1)..] : "";
            if (MessageIdPattern().IsMatch(mm))
            {
                msgId = mm;
                replyAck = aa.Length > 0 && MessageIdPattern().IsMatch(aa) ? aa : null;
                text = text[..open];
            }
        }
        return new AprsInfo { Type = AprsPacketType.Message, DataType = ':', Addressee = addressee, MessageText = text, MessageId = msgId, ReplyAck = replyAck };
    }

    private static readonly string[] MicEStandard = { "Emergency", "Priority", "Special", "Committed", "Returning", "In Service", "En Route", "Off Duty" };
    private static readonly string[] MicECustom = { "Emergency", "Custom-6", "Custom-5", "Custom-4", "Custom-3", "Custom-2", "Custom-1", "Custom-0" };

    /// <summary>Mic-E: latitude etc. from the destination address, longitude/speed/course from the info field.</summary>
    private static AprsInfo ParseMicE(string destination, string info)
    {
        char dti = info[0];
        string dest = destination.Trim().ToUpperInvariant();
        int dash = dest.IndexOf('-');
        if (dash >= 0) dest = dest[..dash];

        AprsInfo Fail(string why) => new() { Type = AprsPacketType.MicE, DataType = dti, Error = why, Comment = info.Length > 9 ? info[9..] : null };

        if (dest.Length != 6) return Fail("Mic-E destination must be 6 characters.");
        if (info.Length < 9) return Fail("Mic-E information field too short.");

        var digits = new char[6];
        int amb = 0, msgBits = 0;
        bool custom = false, standard = false;
        for (int i = 0; i < 6; i++)
        {
            char c = dest[i];
            if (c >= '0' && c <= '9') digits[i] = c;
            else if (c == 'L') { digits[i] = ' '; }
            else if (c >= 'P' && c <= 'Y') { digits[i] = (char)('0' + (c - 'P')); if (i < 3) { msgBits |= 4 >> i; standard = true; } }
            else if (c == 'Z') { digits[i] = ' '; if (i < 3) { msgBits |= 4 >> i; standard = true; } }
            else if (i < 3 && c >= 'A' && c <= 'J') { digits[i] = (char)('0' + (c - 'A')); msgBits |= 4 >> i; custom = true; }
            else if (i < 3 && c == 'K') { digits[i] = ' '; msgBits |= 4 >> i; custom = true; }
            else return Fail($"Invalid Mic-E destination character '{c}' in position {i + 1}.");
            if (digits[i] == ' ') amb++;
        }
        // Ambiguity must blank digits from the right.
        for (int i = 0; i < 5; i++) if (digits[i] == ' ' && digits[i + 1] != ' ') return Fail("Invalid Mic-E position ambiguity.");

        string latDigits = new string(digits).Replace(' ', '0');
        int latDeg = (latDigits[0] - '0') * 10 + (latDigits[1] - '0');
        double latMin = (latDigits[2] - '0') * 10 + (latDigits[3] - '0') + ((latDigits[4] - '0') * 10 + (latDigits[5] - '0')) / 100.0;
        if (latDeg > 89 || latMin >= 60) return Fail("Mic-E latitude out of range.");
        bool north = dest[3] >= 'P' && dest[3] <= 'Z';
        bool lonOffset = dest[4] >= 'P' && dest[4] <= 'Z';
        bool west = dest[5] >= 'P' && dest[5] <= 'Z';
        double lat = (latDeg + latMin / 60.0) * (north ? 1 : -1);

        int d = info[1] - 28;
        if (lonOffset) d += 100;
        if (d >= 180 && d <= 189) d -= 80;
        else if (d >= 190 && d <= 199) d -= 190;
        int m = info[2] - 28;
        if (m >= 60) m -= 60;
        int h = info[3] - 28;
        if (d < 0 || d > 179 || m < 0 || m > 59 || h < 0 || h > 99) return Fail("Mic-E longitude out of range.");
        double lon = (d + (m + h / 100.0) / 60.0) * (west ? -1 : 1);

        int sp = info[4] - 28, dc = info[5] - 28, se = info[6] - 28;
        int? course = null;
        double? speed = null;
        if (sp >= 0 && dc >= 0 && se >= 0)
        {
            int spd = sp * 10 + dc / 10;
            int crs = (dc % 10) * 100 + se;
            if (spd >= 800) spd -= 800;
            if (crs >= 400) crs -= 400;
            speed = spd;
            if (crs >= 1 && crs <= 360) course = crs;
        }

        string rest = info[9..];
        double? alt = null;
        var am = MicEAltitude().Match(rest);
        if (am.Success)
        {
            alt = (Base91(am.Groups[1].Value) - 10000) * 3.28084;
            rest = rest.Remove(am.Groups[1].Index, am.Groups[1].Length + 1);
        }
        rest = rest.TrimEnd('\r', '\n');
        // Kenwood radios mark themselves with a leading '>' or ']' and a trailing '=' or '^'.
        if (rest.Length > 0 && (rest[0] == '>' || rest[0] == ']'))
        {
            rest = rest[1..];
            if (rest.Length > 0 && (rest[^1] == '=' || rest[^1] == '^')) rest = rest[..^1];
        }

        string status = custom && standard ? "Unknown" : (custom ? MicECustom : MicEStandard)[msgBits];
        return new AprsInfo
        {
            Type = AprsPacketType.MicE, DataType = dti, Latitude = lat, Longitude = lon, PositionAmbiguity = amb,
            SymbolCode = info[7], SymbolTable = info[8], CourseDegrees = course, SpeedKnots = speed, AltitudeFeet = alt,
            MicEMessage = status, Comment = rest.Length > 0 ? rest : null,
        };
    }
}
