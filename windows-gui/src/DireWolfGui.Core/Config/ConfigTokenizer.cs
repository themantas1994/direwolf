using System.Text;

namespace DireWolfGui.Core.Config;

/// <summary>One token of a configuration line: its value after quote processing and where it was in the line.</summary>
public readonly record struct ConfigToken(string Value, int Start, int End);

/// <summary>
/// Splits a configuration line exactly like Dire Wolf's <c>split()</c> in src/config.c:
/// CR/LF are dropped, tabs count as spaces, tokens are separated by spaces, a double quote
/// toggles quoting (the quote characters themselves are removed) and a doubled quote inside
/// quotes stands for one quote character.  Lines whose first token starts with '#' or '*'
/// are comments.
/// </summary>
public static class ConfigTokenizer
{
    /// <summary>Token values of the line (what the real parser sees).</summary>
    public static List<string> Split(string line) => Tokenize(line).Select(t => t.Value).ToList();

    /// <summary>Tokens with their positions in the original line.</summary>
    public static List<ConfigToken> Tokenize(string line)
    {
        var result = new List<ConfigToken>();
        int i = 0, n = line.Length;
        static bool IsSpace(char c) => c == ' ' || c == '\t';
        static bool IsDropped(char c) => c == '\r' || c == '\n';
        while (true)
        {
            while (i < n && (IsSpace(line[i]) || IsDropped(line[i]))) i++;
            if (i >= n) break;
            int start = i;
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (; i < n; i++)
            {
                char c = line[i];
                if (IsDropped(c)) continue;
                if (c == '"')
                {
                    if (inQuotes)
                    {
                        int j = i + 1;
                        while (j < n && IsDropped(line[j])) j++;
                        if (j < n && line[j] == '"') { sb.Append('"'); i = j; }
                        else inQuotes = false;
                    }
                    else inQuotes = true;
                }
                else if (IsSpace(c))
                {
                    if (inQuotes) sb.Append(' ');
                    else break;
                }
                else sb.Append(c);
            }
            // An empty token (e.g. "") ends the line for split(), which returns NULL for it.
            if (sb.Length == 0) break;
            result.Add(new ConfigToken(sb.ToString(), start, i));
        }
        return result;
    }

    /// <summary>The value split(NULL,1) would return after skipping <paramref name="skipTokens"/> tokens:
    /// the rest of the line with spaces kept (quotes still processed).</summary>
    public static string? RestOfLine(string line, int skipTokens)
    {
        var tokens = Tokenize(line);
        if (tokens.Count <= skipTokens) return null;
        int start = tokens[skipTokens].Start;
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = start; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\r' || c == '\n') continue;
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else sb.Append(c == '\t' ? ' ' : c);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>True if the real parser ignores the line (blank, or first token starts with # or *).</summary>
    public static bool IsCommentOrBlank(string line)
    {
        var t = Tokenize(line);
        return t.Count == 0 || t[0].Value[0] is '#' or '*';
    }

    /// <summary>Quote a value so split() returns it unchanged as one token (only when needed).</summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
