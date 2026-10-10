using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Wapr;

/// <summary>One WAPR profile as documented in doc/wapr/USAGE.md (values in src/wapr_codec.c).</summary>
public sealed record WaprProfile(
    string Name,
    string Use,
    string Band,
    double SymbolRate,
    int Tones,
    double LowToneHz,
    double HighToneHz,
    double FrameSeconds)
{
    public string ToneDescription => $"{Tones} tones, {LowToneHz:0}-{HighToneHz:0} Hz";
}

public enum DutyLevel { Ok, Elevated, High, Excessive }

/// <summary>Result of an airtime / duty-cycle estimate for a WAPR channel.</summary>
public sealed record WaprDutyEstimate(double SecondsPerFrame, double FramesPerHour, double DutyPercent, DutyLevel Level, string Explanation);

/// <summary>Experimental WAPR modem support: profiles, validation helpers and airtime arithmetic.
/// WAPR is opt-in only; nothing here enables or converts a channel.</summary>
public static class WaprSupport
{
    public const int MaxPayloadBytes = 32;
    /// <summary>The AIRTIME= limit is averaged over this window.</summary>
    public static readonly TimeSpan AirtimeWindow = TimeSpan.FromMinutes(10);

    public const string ExperimentalNotice =
        "WAPR is EXPERIMENTAL. It is not compatible with AX.25/APRS radios, TNCs or digipeaters: a WAPR channel only talks to other " +
        "WAPR stations. Validation so far is by simulation only; real-radio, on-air performance has not been verified. " +
        "Information part at most 32 bytes, no digipeater path, nothing falls back to AX.25.";

    public const string NotSupportedExplanation =
        "This Dire Wolf build does not report WAPR support (no \"wapr\" in the --check-config feature list). " +
        "WAPR exists only in Dire Wolf builds that include the experimental WAPR modem.";

    public static IReadOnlyList<WaprProfile> Profiles { get; } =
    [
        new("F600", "VHF/UHF FM", "VHF/UHF FM", 600, 4, 900, 2700, 0.67),
        new("H150", "HF SSB", "HF SSB", 150, 4, 1275, 1725, 2.67),
        new("R25", "HF, very weak signals", "HF SSB", 25, 8, 1412, 1588, 11.96),
    ];

    /// <summary>Gate type names accepted by WAPRGATE and their bit values (src/wapr_gate.h).</summary>
    public static IReadOnlyDictionary<string, int> GateTypes { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["POS"] = 0x01, ["STATUS"] = 0x02, ["MSG"] = 0x04, ["OBJ"] = 0x08, ["ITEM"] = 0x10, ["WX"] = 0x20, ["TLM"] = 0x40, ["OTHER"] = 0x80, ["ALL"] = 0xFF,
    };

    public static WaprProfile? FindProfile(string? name) =>
        name == null ? null : Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsValidProfile(string? name) => FindProfile(name) != null;

    /// <summary>True if the build reports the "wapr" feature.  <paramref name="explanation"/> says why not.</summary>
    public static bool IsSupported(CheckConfigSummary? summary, out string explanation)
    {
        if (summary == null)
        {
            explanation = "Unknown: the Dire Wolf build has not been checked (or has no --check-config option). " + NotSupportedExplanation;
            return false;
        }
        if (summary.HasFeature("wapr")) { explanation = "This Dire Wolf build includes the experimental WAPR modem."; return true; }
        explanation = NotSupportedExplanation;
        return false;
    }

    public static bool IsSupported(CheckConfigSummary? summary) => IsSupported(summary, out _);

    /// <summary>Parse an AIRTIME=percent option (0 &lt; percent &lt;= 100) as the real parser does.</summary>
    public static bool TryParseAirtime(string token, out double percent)
    {
        percent = 0;
        if (!token.StartsWith("AIRTIME=", StringComparison.OrdinalIgnoreCase)) return false;
        return double.TryParse(token[8..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out percent)
               && percent > 0 && percent <= 100;
    }

    /// <summary>Parse a WAPRGATE types list (comma separated); 0 if any name is unknown or the list is empty.</summary>
    public static int ParseGateTypes(string? list)
    {
        if (string.IsNullOrEmpty(list)) return GateTypes["ALL"];
        int bits = 0;
        foreach (var t in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!GateTypes.TryGetValue(t, out int b)) return 0;
            bits |= b;
        }
        return bits;
    }

    public static string DescribeGateTypes(int bits) =>
        bits == 0xFF ? "ALL" : string.Join(",", GateTypes.Where(kv => kv.Key != "ALL" && (bits & kv.Value) != 0).Select(kv => kv.Key));

    /// <summary>Time on air for one frame including TXDELAY and TXTAIL (10 ms units), which are keyed silence on WAPR.</summary>
    public static double SecondsPerFrame(WaprProfile p, int txDelay = 30, int txTail = 10) => p.FrameSeconds + (txDelay + txTail) / 100.0;

    /// <summary>Estimate the share of time a channel transmits for a given number of frames per hour.</summary>
    public static WaprDutyEstimate EstimateDuty(WaprProfile p, double framesPerHour, int txDelay = 30, int txTail = 10, double? airtimeLimitPercent = null)
    {
        double spf = SecondsPerFrame(p, txDelay, txTail);
        double duty = framesPerHour * spf / 3600.0 * 100.0;
        var level = duty >= 50 ? DutyLevel.Excessive : duty >= 25 ? DutyLevel.High : duty >= 10 ? DutyLevel.Elevated : DutyLevel.Ok;
        string text = $"{p.Name}: {spf:0.##} s per frame, {framesPerHour:0.#} frames/hour ≈ {duty:0.#} % of the time on air.";
        if (airtimeLimitPercent is double lim && duty > lim)
            text += $" This exceeds AIRTIME={lim:0.#} %: frames that do not fit the 10 minute allowance are not sent.";
        text += level switch
        {
            DutyLevel.Excessive => " The channel would be transmitting most of the time; other stations could hardly use it.",
            DutyLevel.High => " That is a large share of a shared channel.",
            DutyLevel.Elevated => " Consider sending less often.",
            _ => "",
        };
        return new WaprDutyEstimate(spf, framesPerHour, duty, level, text);
    }

    /// <summary>Frames that fit in the 10 minute AIRTIME window.</summary>
    public static int FramesPerAirtimeWindow(WaprProfile p, double airtimePercent, int txDelay = 30, int txTail = 10) =>
        (int)Math.Floor(AirtimeWindow.TotalSeconds * airtimePercent / 100.0 / SecondsPerFrame(p, txDelay, txTail));

    /// <summary>Check that a payload (information part) fits a WAPR frame.</summary>
    public static bool FitsPayload(string info, out int bytes)
    {
        bytes = System.Text.Encoding.UTF8.GetByteCount(info);
        return bytes <= MaxPayloadBytes;
    }
}
