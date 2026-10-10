using System.Globalization;
using DireWolfGui.Core.Config;

namespace DireWolfGui.ViewModels;

/// <summary>A configuration line of a given directive, active or disabled by this program.</summary>
public sealed class ServiceLineRow
{
    public required int Index { get; init; }
    public required int LineNumber { get; init; }
    public required string Keyword { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required string Text { get; init; }
    public bool IsDisabled { get; init; }
    public bool IsEnabled => !IsDisabled;
    public string Indent { get; init; } = "";
    public string? TrailingComment { get; init; }
    public string State => IsDisabled ? "disabled" : "active";
    public string Arg(int i) => i < Args.Count ? Args[i] : "";
}

public static class ConfigLineQuery
{
    /// <summary>Active lines of the given directives plus lines disabled by Dire Wolf Station, in file order.</summary>
    public static List<ServiceLineRow> Find(ConfigDocument doc, params string[] directives)
    {
        var set = new HashSet<string>(directives, StringComparer.OrdinalIgnoreCase);
        var rows = new List<ServiceLineRow>();
        foreach (var l in doc.Lines)
        {
            bool disabled = l.Kind == ConfigLineKind.Comment && l.Text.TrimStart().StartsWith(ConfigDocument.DisabledMarker, StringComparison.Ordinal);
            if (!l.IsDirective && !disabled) continue;
            string body = disabled ? l.Text.TrimStart()[ConfigDocument.DisabledMarker.Length..] : l.Text;
            if (disabled)
            {
                int reason = body.LastIndexOf("   # ", StringComparison.Ordinal);
                if (reason >= 0) body = body[..reason];
            }
            var tokens = ConfigTokenizer.Split(body);
            if (tokens.Count == 0) continue;
            string name = DirectiveCatalog.Find(tokens[0])?.Name ?? tokens[0].ToUpperInvariant();
            if (!set.Contains(name)) continue;
            int hash = tokens.FindIndex(1, t => t.StartsWith('#'));
            var args = (hash < 0 ? tokens.Skip(1) : tokens.Skip(1).Take(hash - 1)).ToList();
            rows.Add(new ServiceLineRow
            {
                Index = l.Index,
                LineNumber = l.LineNumber,
                Keyword = name,
                Args = args,
                Text = PasscodeMask.MaskLine(body.Trim()),
                IsDisabled = disabled,
                Indent = disabled ? "" : l.Text[..(l.Text.Length - l.Text.TrimStart(' ', '\t').Length)],
                TrailingComment = disabled ? null : l.TrailingComment,
            });
        }
        return rows;
    }

    /// <summary>Text for a replacement line: the existing indentation and inline '#' text are kept.</summary>
    public static string Replacement(ServiceLineRow row, string body) => row.Indent + body + (row.TrailingComment ?? "");
}

/// <summary>keyword=value options of beacon lines (PBEACON, OBEACON, TBEACON, CBEACON, IBEACON).</summary>
public static class BeaconOptions
{
    public static readonly string[] Kinds = ["PBEACON", "OBEACON", "TBEACON", "CBEACON", "IBEACON"];

    /// <summary>Options in order; a token without '=' is kept with an empty value.</summary>
    public static List<KeyValuePair<string, string>> Parse(IEnumerable<string> tokens)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var t in tokens)
        {
            int eq = t.IndexOf('=');
            list.Add(eq > 0 ? new(t[..eq], t[(eq + 1)..]) : new(t, ""));
        }
        return list;
    }

    public static string? Get(IEnumerable<KeyValuePair<string, string>> options, string key) =>
        options.LastOrDefault(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Tokens for the configuration line: "key=value", quoted as a whole when needed (as the template does).</summary>
    public static List<string> ToTokens(IEnumerable<KeyValuePair<string, string>> options) =>
        options.Where(o => o.Key.Length > 0).Select(o => ConfigTokenizer.Quote(o.Value.Length == 0 && !o.Key.Contains('=') ? o.Key : o.Key + "=" + o.Value)).ToList();

    /// <summary>"key=value" lines (one per line) for the editor.</summary>
    public static string ToEditorText(IEnumerable<KeyValuePair<string, string>> options) =>
        string.Join(Environment.NewLine, options.Select(o => o.Value.Length == 0 ? o.Key : o.Key + "=" + o.Value));

    /// <summary>Parse editor text: one key=value per line, blank lines ignored.</summary>
    public static List<KeyValuePair<string, string>> FromEditorText(string text) =>
        Parse(text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>Interval in seconds from "minutes" or "minutes:seconds", or null when empty/invalid.</summary>
    public static int? IntervalSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        if (!v.All(c => char.IsAsciiDigit(c) || c == ':') || v.Count(c => c == ':') > 1) return null;
        return ConfigFacts.ParseInterval(v);
    }
}

/// <summary>A forwarding-related line observed in Dire Wolf's output.</summary>
public sealed record ForwardingEvent(DateTimeOffset Time, string Kind, string KindBrush, string Text);

/// <summary>Classifies console lines that show the station forwarding traffic (pure observation of the log).</summary>
public static class ForwardingLog
{
    /// <summary>Returns (kind, brush key) for a forwarding line, or null.</summary>
    public static (string Kind, string Brush)? Classify(string line, bool includeHeardViaDigipeater)
    {
        string t = line.TrimStart();
        if (t.StartsWith("[rx>ig]", StringComparison.Ordinal)) return ("Radio → APRS-IS", "IgateColor");
        if (t.StartsWith("[ig>tx]", StringComparison.Ordinal)) return ("APRS-IS → radio", "Tx");
        if (t.StartsWith("[ig]", StringComparison.Ordinal)) return ("From APRS-IS", "IgateColor");
        if (t.Contains(">is]", StringComparison.Ordinal)) return ("To APRS-IS", "IgateColor");
        if (t.StartsWith("[WAPR gate", StringComparison.Ordinal)) return ("WAPR gate", "Gateway");
        if (t.StartsWith("EXPERIMENTAL WAPR gateway", StringComparison.Ordinal)) return ("WAPR gate", "Gateway");
        if (IsHighPriorityTransmit(t)) return ("Transmitted (digipeat / priority)", "Tx");
        if (t.Contains("digipeat", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("Digipeater ", StringComparison.Ordinal)) return ("Digipeater", "Tx");
        if (includeHeardViaDigipeater && t.StartsWith("Digipeater ", StringComparison.Ordinal)) return ("Heard via a digipeater", "Neutral");
        return null;
    }

    /// <summary>"[0H] ..." : a high-priority transmission, which is what Dire Wolf uses for digipeated frames.</summary>
    public static bool IsHighPriorityTransmit(string t)
    {
        if (t.Length < 4 || t[0] != '[') return false;
        int i = 1;
        while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
        return i > 1 && i < t.Length && t[i] == 'H';
    }
}

public static class IntervalText
{
    public static string Format(int? seconds) => seconds is int s ? StationServicesAnalyzer.Interval(s) : "default";

    public static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
}
