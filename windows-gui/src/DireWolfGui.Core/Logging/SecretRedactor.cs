using System.Text.RegularExpressions;

namespace DireWolfGui.Core.Logging;

/// <summary>Masks APRS-IS passcodes and similar secrets before text is stored or displayed.</summary>
public static partial class SecretRedactor
{
    public const string Mask = "*****";

    // IGLOGIN WB2OSZ-5 123456
    [GeneratedRegex(@"(?i)\b(IGLOGIN\s+\S+\s+)(-?\d+)")]
    private static partial Regex IgLogin();

    // APRS-IS login line: "user CALL pass 12345 vers ..."
    [GeneratedRegex(@"(?i)\b(pass\s+)(-?\d+)")]
    private static partial Regex Pass();

    // passcode=12345, passcode: 12345, password=xyz
    [GeneratedRegex(@"(?i)\b(passcode|password|passwd)(\s*[=:]\s*)(\S+)")]
    private static partial Regex KeyValue();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        // Cheap pre-check: nearly every line has none of these words.
        if (text.IndexOf("pass", StringComparison.OrdinalIgnoreCase) < 0 &&
            text.IndexOf("IGLOGIN", StringComparison.OrdinalIgnoreCase) < 0)
            return text;
        text = IgLogin().Replace(text, m => m.Groups[1].Value + Mask);
        text = Pass().Replace(text, m => m.Groups[1].Value + Mask);
        text = KeyValue().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Mask);
        return text;
    }
}
