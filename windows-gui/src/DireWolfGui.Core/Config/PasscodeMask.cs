using System.Text.RegularExpressions;

namespace DireWolfGui.Core.Config;

/// <summary>
/// Reversible masking of APRS-IS passcodes for the raw text editor: the passcode token of every
/// IGLOGIN line (also commented / disabled ones) is shown as *****, and put back when the text is
/// applied, matched by callsign.
/// </summary>
public static partial class PasscodeMask
{
    public const string Mask = "*****";

    [GeneratedRegex(@"(?i)(\bIGLOGIN[ \t]+(?<call>\S+)[ \t]+)(?<pass>[^\s#]+)")]
    private static partial Regex IgLogin();

    public static string MaskText(string text, List<(string Call, string Secret)> secrets)
    {
        return IgLogin().Replace(text, m =>
        {
            string pass = m.Groups["pass"].Value;
            if (pass == Mask) return m.Value;
            secrets.Add((m.Groups["call"].Value, pass));
            return m.Groups[1].Value + Mask;
        });
    }

    public static string UnmaskText(string text, IReadOnlyList<(string Call, string Secret)> secrets)
    {
        if (secrets.Count == 0) return text;
        var used = new bool[secrets.Count];
        int order = 0;
        return IgLogin().Replace(text, m =>
        {
            if (m.Groups["pass"].Value != Mask) return m.Value;
            string call = m.Groups["call"].Value;
            int idx = -1;
            for (int i = 0; i < secrets.Count; i++)
                if (!used[i] && string.Equals(secrets[i].Call, call, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx < 0)
                for (int i = order; i < secrets.Count; i++)
                    if (!used[i]) { idx = i; break; }
            if (idx < 0) return m.Value;
            used[idx] = true;
            order = idx + 1;
            return m.Groups[1].Value + secrets[idx].Secret;
        });
    }

    /// <summary>One line for display: the IGLOGIN passcode masked.</summary>
    public static string MaskLine(string line) => IgLogin().Replace(line, m => m.Groups[1].Value + Mask);
}
