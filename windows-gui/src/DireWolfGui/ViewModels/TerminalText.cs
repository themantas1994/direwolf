using System.Globalization;
using System.Text;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

public enum TerminalLineKind { Received, Sent, System, Error }

/// <summary>One line of terminal output. Received text may arrive in pieces, so the last line can grow.</summary>
public sealed class TerminalLine : ObservableObject
{
    public TerminalLine(TerminalLineKind kind, string text, DateTimeOffset time)
    {
        Kind = kind;
        _text = text;
        Time = time;
    }

    public TerminalLineKind Kind { get; }
    public DateTimeOffset Time { get; }

    /// <summary>True while more text may be appended (received text without its line end yet).</summary>
    public bool IsOpen { get; set; }

    private string _text;
    public string Text { get => _text; set => Set(ref _text, value); }

    /// <summary>Theme brush prefix: received, sent echo and notices each have their own colour.</summary>
    public string BrushKind => Kind switch
    {
        TerminalLineKind.Received => "Rx",
        TerminalLineKind.Sent => "Tx",
        TerminalLineKind.Error => "Error",
        _ => "Info",
    };

    public string Prefix => Kind switch { TerminalLineKind.Sent => "> ", TerminalLineKind.System => "", TerminalLineKind.Error => "! ", _ => "" };

    public override string ToString() => Prefix + Text;
}

/// <summary>Pure text handling for the packet terminal (line ends, control characters, partial lines).</summary>
public static class TerminalText
{
    /// <summary>
    /// CR LF, LF CR, lone CR and lone LF all become "\n"; tabs are kept; other control characters
    /// (bell, backspace, escape sequences' ESC, NUL) are shown as &lt;0xNN&gt; except NUL which is dropped.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\r') i++;
            }
            else if (c == '\0') { }
            else if (c == '\t' || c >= 0x20 && c != 0x7F) sb.Append(c);
            else sb.Append("<0x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture)).Append('>');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Appends text to the transcript: complete lines become new entries, text after the last line end stays
    /// open so the next piece of received text continues it. Sent / system text always forms whole lines.
    /// Returns the number of lines added.
    /// </summary>
    public static int Append(IList<TerminalLine> lines, TerminalLineKind kind, string text, DateTimeOffset time)
    {
        text = Normalize(text);
        int added = 0;
        var last = lines.Count > 0 ? lines[^1] : null;
        if (kind != TerminalLineKind.Received)
        {
            if (last is { IsOpen: true }) last.IsOpen = false;
            foreach (var part in text.TrimEnd('\n').Split('\n'))
            {
                lines.Add(new TerminalLine(kind, part, time));
                added++;
            }
            return added;
        }
        if (text.Length == 0) return 0;
        var pieces = text.Split('\n');
        for (int i = 0; i < pieces.Length; i++)
        {
            bool isLast = i == pieces.Length - 1;
            string piece = pieces[i];
            if (isLast && piece.Length == 0) break;   // text ended with a line end
            if (i == 0 && last is { IsOpen: true, Kind: TerminalLineKind.Received })
            {
                last.Text += piece;
                last.IsOpen = isLast;
                continue;
            }
            if (i == 0 && last is { IsOpen: true }) last.IsOpen = false;
            lines.Add(new TerminalLine(TerminalLineKind.Received, piece, time) { IsOpen = isLast });
            added++;
        }
        return added;
    }

    /// <summary>Digipeaters from "WIDE1-1, RELAY" style text; null + error when invalid (at most 7 for connected mode).</summary>
    public static IReadOnlyList<string>? ParseVia(string? text, out string? error)
    {
        error = null;
        var parts = (text ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToUpperInvariant()).ToList();
        if (parts.Count > 7) { error = "Connected mode allows at most 7 digipeaters."; return null; }
        foreach (var p in parts)
            if (!Core.Packets.Ax25Address.TryParse(p, out _)) { error = $"\"{p}\" is not a valid callsign."; return null; }
        return parts;
    }

    /// <summary>Plain text transcript with local times.</summary>
    public static string Transcript(IEnumerable<TerminalLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
            sb.Append(l.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("  ").Append(l).Append(Environment.NewLine);
        return sb.ToString();
    }
}
