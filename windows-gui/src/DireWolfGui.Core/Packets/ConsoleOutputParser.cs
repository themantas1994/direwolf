using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Logging;

namespace DireWolfGui.Core.Packets;

public enum ConsoleNoticeKind
{
    Info, Error, Warning, WaprLink, WaprGate, IGate, ServerListening, ClientConnected, ClientDisconnected,
    AudioDevice, Startup, Shutdown, AudioInputEnded, ConfigProblem,
}

/// <summary>A non-packet event recognised in Dire Wolf's output.</summary>
public sealed record ConsoleNotice(ConsoleNoticeKind Kind, DateTimeOffset Time, string Text)
{
    /// <summary>TCP port for ServerListening/ClientConnected.</summary>
    public int? Port { get; init; }
    /// <summary>"AGW" or "KISS" for server/client notices.</summary>
    public string? Protocol { get; init; }
    public int? Channel { get; init; }
    /// <summary>Client slot number for client notices.</summary>
    public int? Client { get; init; }
}

/// <summary>Audio level of a received frame ("audio level = 50(14/14)").</summary>
public sealed record AudioLevelReading(DateTimeOffset Time, int? Channel, string Station, int Level, int? Mark, int? Space, string Text);

/// <summary>Periodic statistics printed with -a ("ADEVICE0: Sample rate approx. 44.1 k, 0 errors, receive audio level CH0 73").</summary>
public sealed record AudioStatisticsReading(DateTimeOffset Time, int Device, double SampleRateKHz, int Errors, IReadOnlyDictionary<int, int> ChannelLevels);

public enum ConsoleLineKind { Blank, Heard, Packet, Decoded, Notice, Other }

/// <summary>
/// Stateful parser for Dire Wolf console output (run with -t 0). Feed it every line in order.
/// Packets are reported as soon as their "[chan] SRC>DST:info" line arrives; the decode lines that
/// follow are appended to <see cref="PacketRecord.DecodedLines"/> of that same record.
/// </summary>
public sealed partial class ConsoleOutputParser
{
    private PendingHeard? _heard;
    private PacketRecord? _decoding;

    private sealed record PendingHeard(string Station, string? Probably, int Level, int? Mark, int? Space, string LevelText, string Rest, bool Digipeater);

    public event Action<PacketRecord>? PacketParsed;
    public event Action<AudioLevelReading>? AudioLevel;
    public event Action<AudioStatisticsReading>? AudioStatistics;
    public event Action<ConsoleNotice>? Notice;

    /// <summary>Version text from the startup banner, once seen (e.g. "Release 1.8.2, November 2025").</summary>
    public string? Version { get; private set; }

    [GeneratedRegex(@"^(?<dig>Digipeater )?(?<call>\S+)(?: \(probably (?<prob>\S+)\))? audio level = (?<lvl>-?\d+)(?:\((?<mark>[+-]?\d+)/(?<space>[+-]?\d+)\))?(?<rest>.*)$")]
    private static partial Regex HeardLine();

    [GeneratedRegex(@"^\[(?<label>[^\]\s]+)(?<ts> [^\]]*)?\] (?<rest>.*)$")]
    private static partial Regex BracketLine();

    [GeneratedRegex(@"^(?<chan>\d+)(?:\.(?<sub>\d+|dtmf|is|AIS))?(?:\.(?<slice>\d+))?$")]
    private static partial Regex ReceiveLabel();

    [GeneratedRegex(@"^(?<chan>\d+)(?<prio>[HL])$")]
    private static partial Regex TransmitLabel();

    [GeneratedRegex(@"^(?<chan>\d+)>(?<what>is|nt)$")]
    private static partial Regex ToNetLabel();

    [GeneratedRegex(@"^(?<src>[^>:\s]+)>(?<dst>[^,:\s]+)(?<path>(?:,[^,:\s]+)*):(?<info>.*)$")]
    private static partial Regex Monitor();

    [GeneratedRegex(@"^\((?<desc>(?:I|RR|RNR|REJ|SREJ|SABME|SABM|DISC|DM|UA|FRMR|UI|XID|TEST) (?:cmd|res|cc=00|cc=11)(?:, [a-z/()]+=(?:0x[0-9a-f]+|\d+))*|U other\?\?\?)\)")]
    private static partial Regex FrameDesc();

    [GeneratedRegex(@"<0x([0-9a-fA-F]{2})>")]
    private static partial Regex HexEscape();

    [GeneratedRegex(@"^Ready to accept (?<proto>AGW|KISS TCP) client application (?<client>\d+) on port (?<port>\d+)(?: \(radio channel (?<chan>\d+)\))?")]
    private static partial Regex ReadyToAccept();

    [GeneratedRegex(@"^Attached to (?<proto>AGW|KISS TCP) client application (?<client>\d+)(?: on port (?<port>\d+))?")]
    private static partial Regex Attached();

    [GeneratedRegex(@"(?<proto>AGW|KISS) client application (?<client>\d+)|KISS TCP port (?<port>\d+) client (?<client2>\d+)")]
    private static partial Regex ClientRef();

    [GeneratedRegex(@"^ADEVICE(?<dev>\d+): Sample rate approx\. (?<rate>[\d.]+) k, (?<err>\d+) errors, receive audio levels? (?<levels>.*)$")]
    private static partial Regex AudioStats();

    [GeneratedRegex(@"CH(?<ch>\d+) (?<lvl>\d+)")]
    private static partial Regex ChannelLevel();

    [GeneratedRegex(@"^Dire Wolf (?<v>(?:Release|version|DEVELOPMENT version) .*)$")]
    private static partial Regex Banner();

    [GeneratedRegex(@"^Audio (?:input device for receive|out device for transmit): .*?(?:\(channel (?<chan>\d+)\))?\s*$")]
    private static partial Regex AudioDeviceLine();

    [GeneratedRegex(@"^WAPR:? channel (?<chan>\d+)")]
    private static partial Regex WaprChannel();

    [GeneratedRegex(@"SNR -?[\d.]+ dB, [+-]?[\d.]+ Hz")]
    private static partial Regex WaprSpectrum();

    /// <summary>Processes one output line. Returns what kind of line it was.</summary>
    public ConsoleLineKind ProcessLine(string line, DateTimeOffset time)
    {
        line ??= "";
        line = line.TrimEnd('\r');
        if (line.Length == 0 || string.IsNullOrWhiteSpace(line))
        {
            _decoding = null;
            _heard = null;
            return ConsoleLineKind.Blank;
        }

        var hm = HeardLine().Match(line);
        if (hm.Success)
        {
            _decoding = null;
            _heard = new PendingHeard(hm.Groups["call"].Value, hm.Groups["prob"].Success ? hm.Groups["prob"].Value : null,
                int.Parse(hm.Groups["lvl"].Value, CultureInfo.InvariantCulture),
                hm.Groups["mark"].Success ? int.Parse(hm.Groups["mark"].Value, CultureInfo.InvariantCulture) : null,
                hm.Groups["space"].Success ? int.Parse(hm.Groups["space"].Value, CultureInfo.InvariantCulture) : null,
                line[(line.IndexOf("audio level = ", StringComparison.Ordinal) + 14)..].Split(' ')[0],
                hm.Groups["rest"].Value, hm.Groups["dig"].Success);
            return ConsoleLineKind.Heard;
        }

        if (line.StartsWith("Audio input level is too", StringComparison.Ordinal))
        {
            // Printed between the heard line and the packet line: keep the pending heard info.
            Notice?.Invoke(new ConsoleNotice(ConsoleNoticeKind.Warning, time, line) { Protocol = "audio" });
            return ConsoleLineKind.Notice;
        }

        var bm = BracketLine().Match(line);
        if (bm.Success)
        {
            var kind = HandleBracketLine(line, bm, time);
            if (kind != ConsoleLineKind.Other) return kind;
        }

        var notice = RecognizeNotice(line, time);
        if (notice != null)
        {
            // A recognised status line ends any packet decode block, except harmless info lines
            // (e.g. "Opening log file") which Dire Wolf prints in the middle of a decode.
            if (notice.Kind != ConsoleNoticeKind.Info)
            {
                _decoding = null;
                _heard = null;
            }
            Notice?.Invoke(notice);
            return ConsoleLineKind.Notice;
        }

        if (_decoding != null)
        {
            _decoding.AppendDecodedLine(line);
            return ConsoleLineKind.Decoded;
        }
        _heard = null;
        var (sev, _) = LogClassifier.Classify(line);
        if (sev is LogSeverity.Error or LogSeverity.Warning)
        {
            bool config = line.StartsWith("Line ", StringComparison.Ordinal) || line.StartsWith("Config file", StringComparison.Ordinal)
                || line.Contains("configuration file", StringComparison.OrdinalIgnoreCase);
            Notice?.Invoke(new ConsoleNotice(config ? ConsoleNoticeKind.ConfigProblem : sev == LogSeverity.Error ? ConsoleNoticeKind.Error : ConsoleNoticeKind.Warning, time, line));
            return ConsoleLineKind.Notice;
        }
        return ConsoleLineKind.Other;
    }

    private ConsoleLineKind HandleBracketLine(string line, Match bm, DateTimeOffset time)
    {
        string label = bm.Groups["label"].Value;
        string rest = bm.Groups["rest"].Value;

        if (line.StartsWith("[WAPR gate", StringComparison.Ordinal))
        {
            _decoding = null;
            Notice?.Invoke(new ConsoleNotice(ConsoleNoticeKind.WaprGate, time, line));
            return ConsoleLineKind.Notice;
        }
        if ((label == "ig" || label == "rx>ig") && !Monitor().IsMatch(rest))
        {
            // "[ig] # logresp N0CALL verified, server T2XYZ" and similar server/heartbeat lines.
            _decoding = null;
            Notice?.Invoke(new ConsoleNotice(ConsoleNoticeKind.IGate, time, line));
            return ConsoleLineKind.Notice;
        }

        int? chan = null;
        PacketDirection dir;
        PacketOrigin origin;
        var rx = ReceiveLabel().Match(label);
        var tx = TransmitLabel().Match(label);
        var tn = ToNetLabel().Match(label);
        if (rx.Success)
        {
            chan = int.Parse(rx.Groups["chan"].Value, CultureInfo.InvariantCulture);
            dir = PacketDirection.Received;
            origin = rx.Groups["sub"].Value switch { "is" => PacketOrigin.FromAprsIs, "dtmf" => PacketOrigin.Dtmf, "AIS" => PacketOrigin.Ais, _ => PacketOrigin.Radio };
            if (rest.StartsWith('"')) { _decoding = null; return ConsoleLineKind.Other; } // "[0.dtmf] "..." is a transmitted tone sequence
        }
        else if (tx.Success)
        {
            chan = int.Parse(tx.Groups["chan"].Value, CultureInfo.InvariantCulture);
            dir = PacketDirection.Transmitted;
            origin = PacketOrigin.Local;
        }
        else if (tn.Success)
        {
            chan = int.Parse(tn.Groups["chan"].Value, CultureInfo.InvariantCulture);
            dir = PacketDirection.Transmitted;
            origin = tn.Groups["what"].Value == "is" ? PacketOrigin.ToAprsIs : PacketOrigin.NetworkTnc;
        }
        else if (label == "ig") { dir = PacketDirection.Received; origin = PacketOrigin.FromAprsIs; }
        else if (label == "rx>ig") { dir = PacketDirection.Transmitted; origin = PacketOrigin.ToAprsIs; }
        else if (label == "ig>tx") { dir = PacketDirection.Transmitted; origin = PacketOrigin.IgateToRadio; }
        else return ConsoleLineKind.Other;

        var mm = Monitor().Match(rest);
        if (!mm.Success) return ConsoleLineKind.Other;

        string info = mm.Groups["info"].Value;
        string? desc = null;
        var dm = FrameDesc().Match(info);
        if (dm.Success)
        {
            desc = dm.Groups["desc"].Value;
            info = info[dm.Length..];
            if (info.StartsWith(' ')) info = info[1..];
        }
        info = Unescape(info);
        string dst = mm.Groups["dst"].Value;
        var heard = dir == PacketDirection.Received ? _heard : null;
        _heard = null;

        string? fec = null, wapr = null;
        if (heard != null)
        {
            string r = heard.Rest;
            if (r.Contains(" WAPR ", StringComparison.Ordinal)) fec = "WAPR";
            else if (r.Contains(" FX.25 ", StringComparison.Ordinal)) fec = "FX.25";
            else if (r.Contains(" IL2P ", StringComparison.Ordinal)) fec = "IL2P";
            else
            {
                int a = r.IndexOf('['), b = a >= 0 ? r.IndexOf(']', a) : -1;
                if (a >= 0 && b > a) fec = r[a..(b + 1)];
            }
            var sm = WaprSpectrum().Match(r);
            if (sm.Success) wapr = sm.Value;
        }

        var rec = new PacketRecord
        {
            Time = time,
            Channel = chan,
            Label = label,
            Direction = dir,
            Origin = origin,
            SourceKind = PacketSourceKind.Console,
            Source = mm.Groups["src"].Value,
            Destination = dst,
            Path = mm.Groups["path"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries),
            Info = info,
            FrameDescription = desc,
            Heard = heard?.Station,
            AudioLevel = heard?.Level,
            AudioLevelText = heard?.LevelText,
            Fec = fec,
            IsWapr = fec == "WAPR",
            WaprDetails = wapr,
            RawText = line,
            Aprs = desc == null ? AprsParser.Parse(dst, info) : null,
        };
        _decoding = rec;
        if (heard != null)
            AudioLevel?.Invoke(new AudioLevelReading(time, chan, heard.Station, heard.Level, heard.Mark, heard.Space, heard.LevelText));
        PacketParsed?.Invoke(rec);
        return ConsoleLineKind.Packet;
    }

    /// <summary>Turns Dire Wolf's "&lt;0x0a&gt;" escapes back into characters (UTF-8 aware).</summary>
    public static string Unescape(string text)
    {
        if (!text.Contains("<0x", StringComparison.Ordinal)) return text;
        var bytes = new List<byte>(text.Length);
        int last = 0;
        foreach (Match m in HexEscape().Matches(text))
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(text[last..m.Index]));
            bytes.Add(byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            last = m.Index + m.Length;
        }
        bytes.AddRange(Encoding.UTF8.GetBytes(text[last..]));
        var arr = bytes.ToArray();
        try { return new UTF8Encoding(false, true).GetString(arr); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(arr); }
    }

    private ConsoleNotice? RecognizeNotice(string line, DateTimeOffset time)
    {
        Match m;
        if ((m = ReadyToAccept().Match(line)).Success)
            return new ConsoleNotice(ConsoleNoticeKind.ServerListening, time, line)
            {
                Protocol = m.Groups["proto"].Value == "AGW" ? "AGW" : "KISS",
                Port = int.Parse(m.Groups["port"].Value, CultureInfo.InvariantCulture),
                Client = int.Parse(m.Groups["client"].Value, CultureInfo.InvariantCulture),
                Channel = m.Groups["chan"].Success ? int.Parse(m.Groups["chan"].Value, CultureInfo.InvariantCulture) : null,
            };
        if ((m = Attached().Match(line)).Success)
            return new ConsoleNotice(ConsoleNoticeKind.ClientConnected, time, line)
            {
                Protocol = m.Groups["proto"].Value == "AGW" ? "AGW" : "KISS",
                Client = int.Parse(m.Groups["client"].Value, CultureInfo.InvariantCulture),
                Port = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value, CultureInfo.InvariantCulture) : null,
            };
        if ((line.Contains("Closing connection", StringComparison.Ordinal) || line.Contains("Error getting message header", StringComparison.Ordinal))
            && (m = ClientRef().Match(line)).Success)
            return new ConsoleNotice(ConsoleNoticeKind.ClientDisconnected, time, line)
            {
                Protocol = m.Groups["proto"].Success ? m.Groups["proto"].Value : "KISS",
                Client = int.Parse(m.Groups["client"].Success ? m.Groups["client"].Value : m.Groups["client2"].Value, CultureInfo.InvariantCulture),
                Port = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value, CultureInfo.InvariantCulture) : null,
            };
        if ((m = AudioStats().Match(line)).Success)
        {
            var levels = new Dictionary<int, int>();
            foreach (Match c in ChannelLevel().Matches(m.Groups["levels"].Value))
                levels[int.Parse(c.Groups["ch"].Value, CultureInfo.InvariantCulture)] = int.Parse(c.Groups["lvl"].Value, CultureInfo.InvariantCulture);
            var stats = new AudioStatisticsReading(time, int.Parse(m.Groups["dev"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["rate"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["err"].Value, CultureInfo.InvariantCulture), levels);
            AudioStatistics?.Invoke(stats);
            return new ConsoleNotice(ConsoleNoticeKind.AudioDevice, time, line) { Channel = levels.Keys.FirstOrDefault() };
        }
        if ((m = Banner().Match(line)).Success)
        {
            Version = m.Groups["v"].Value.Trim();
            return new ConsoleNotice(ConsoleNoticeKind.Startup, time, line);
        }
        if (line.StartsWith("Includes optional support for:", StringComparison.Ordinal))
            return new ConsoleNotice(ConsoleNoticeKind.Startup, time, line);
        if (line == "QRT")
            return new ConsoleNotice(ConsoleNoticeKind.Shutdown, time, line);
        if (line.StartsWith("End of file on stdin", StringComparison.Ordinal))
            return new ConsoleNotice(ConsoleNoticeKind.AudioInputEnded, time, line);
        if ((m = AudioDeviceLine().Match(line)).Success)
            return new ConsoleNotice(ConsoleNoticeKind.AudioDevice, time, line)
            { Channel = m.Groups["chan"].Success ? int.Parse(m.Groups["chan"].Value, CultureInfo.InvariantCulture) : null };
        if (line.StartsWith("Could not open audio device", StringComparison.Ordinal))
            return new ConsoleNotice(ConsoleNoticeKind.Error, time, line) { Protocol = "audio" };
        if ((m = WaprChannel().Match(line)).Success)
            return new ConsoleNotice(ConsoleNoticeKind.WaprLink, time, line) { Channel = int.Parse(m.Groups["chan"].Value, CultureInfo.InvariantCulture) };
        if (line.StartsWith("Opening log file", StringComparison.Ordinal) || line.StartsWith("Reading config file", StringComparison.Ordinal))
            return new ConsoleNotice(ConsoleNoticeKind.Info, time, line);
        if (line.Contains("IGate", StringComparison.OrdinalIgnoreCase) || line.Contains("APRS-IS", StringComparison.Ordinal))
            return new ConsoleNotice(LogClassifier.Classify(line).Severity == LogSeverity.Error ? ConsoleNoticeKind.Error : ConsoleNoticeKind.IGate, time, line);
        return null;
    }
}
