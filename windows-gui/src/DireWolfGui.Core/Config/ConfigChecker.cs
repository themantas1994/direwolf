using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DireWolfGui.Core.Config;

public enum CheckConfigStatus
{
    /// <summary>The real parser ran and reported no diagnostics.</summary>
    Ok,
    /// <summary>The real parser ran and printed errors or warnings.</summary>
    Diagnostics,
    /// <summary>This direwolf build has no --check-config option.</summary>
    Unsupported,
    /// <summary>The executable could not be started, timed out or produced unexpected output.</summary>
    Failed,
}

public sealed class CheckConfigResult
{
    public CheckConfigStatus Status { get; init; }
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; init; } = [];
    /// <summary>Informational lines from the parser (e.g. the WAPR experimental notice).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    public CheckConfigSummary? Summary { get; init; }
    /// <summary>Complete output with any APRS-IS passcode replaced by asterisks.</summary>
    public string RawOutput { get; init; } = "";
    public int? ExitCode { get; init; }
    public bool TimedOut { get; init; }
    /// <summary>Plain-language explanation for Unsupported / Failed.</summary>
    public string? Message { get; init; }
    public bool IsSupported => Status is not CheckConfigStatus.Unsupported;
}

/// <summary>
/// Validates a configuration with the real Dire Wolf parser: runs
/// <c>direwolf -t 0 --check-config -c file</c> (nothing is opened: no audio, PTT or network)
/// and turns the output into diagnostics and a <see cref="CheckConfigSummary"/>.
/// </summary>
public static partial class ConfigChecker
{
    public const string UnsupportedMessage =
        "Real-parser validation unavailable for this Dire Wolf build (it has no --check-config option). Only the GUI's own checks were made.";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public static async Task<CheckConfigResult> CheckFileAsync(string direwolfExe, string configPath,
        TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        string full = Path.GetFullPath(configPath);
        var secrets = File.Exists(full) ? FindSecrets(ConfigDocument.Load(full)) : [];
        var psi = new ProcessStartInfo(direwolfExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(full)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-t", "0", "--check-config", "-c", full }) psi.ArgumentList.Add(a);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new CheckConfigResult { Status = CheckConfigStatus.Failed, Message = $"Could not start \"{direwolfExe}\": {e.Message}" };
        }
        using (proc)
        {
            proc.StandardInput.Close();
            var sb = new StringBuilder();
            var lockObj = new object();
            var outTask = PumpAsync(proc.StandardOutput, sb, lockObj);
            var errTask = PumpAsync(proc.StandardError, sb, lockObj);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(timeout ?? DefaultTimeout);
            bool timedOut = false;
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancel.IsCancellationRequested;
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (!timedOut) throw;
            }
            catch (TimeoutException) { }
            string output;
            lock (lockObj) output = sb.ToString();
            if (timedOut)
                return new CheckConfigResult
                {
                    Status = CheckConfigStatus.Failed, TimedOut = true, RawOutput = Redact(output, secrets),
                    Message = $"direwolf --check-config did not finish within {(timeout ?? DefaultTimeout).TotalSeconds:0} s.",
                };
            return ParseOutput(output, proc.ExitCode, secrets);
        }
    }

    /// <summary>Check an in-memory document by writing it to a temporary file next to nothing else.</summary>
    public static async Task<CheckConfigResult> CheckDocumentAsync(string direwolfExe, ConfigDocument doc,
        TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "DireWolfStation-check-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "direwolf.conf");
        try
        {
            await File.WriteAllBytesAsync(path, doc.ToBytes(), cancel).ConfigureAwait(false);
            return await CheckFileAsync(direwolfExe, path, timeout, cancel).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task PumpAsync(StreamReader r, StringBuilder sb, object lockObj)
    {
        string? line;
        while ((line = await r.ReadLineAsync().ConfigureAwait(false)) != null)
            lock (lockObj) sb.Append(line).Append('\n');
    }

    // ------------------------------------------------------------------ secrets

    /// <summary>APRS-IS passcodes in the document (IGLOGIN second argument).</summary>
    public static IReadOnlyList<string> FindSecrets(ConfigDocument doc) =>
        doc.FindDirectives("IGLOGIN").Select(l => l.AllTokens.Count > 1 ? l.AllTokens[1] : null)
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();

    [GeneratedRegex(@"(?i)(\bIGLOGIN\s+\S+\s+)(\S+)")]
    private static partial Regex IgLoginPattern();

    [GeneratedRegex(@"(?i)(\bpass(?:code)?\s*[=:]?\s*)(-?\d{1,6})\b")]
    private static partial Regex PasscodePattern();

    /// <summary>Remove passcodes: known secret values, "IGLOGIN call NNNN" and "pass NNNN" forms.</summary>
    public static string Redact(string text, IEnumerable<string>? secrets = null)
    {
        foreach (var s in secrets ?? [])
            // Very short values ("-1" means receive-only) would match unrelated numbers.
            if (s.Length >= 3) text = Regex.Replace(text, @"(?<![\w-])" + Regex.Escape(s) + @"(?![\w])", "*****");
        text = IgLoginPattern().Replace(text, m => m.Groups[1].Value + "*****");
        text = PasscodePattern().Replace(text, m => m.Groups[1].Value + "*****");
        return text;
    }

    // ------------------------------------------------------------------ output parsing

    [GeneratedRegex(@"(?i)\bline\s+(\d+)")]
    private static partial Regex LineNumber();

    private static readonly string[] IgnoredPrefixes =
    [
        "Dire Wolf ", "Includes optional support", "Why are you running this as root", "Dire Wolf requires only privileges",
        "Running this as root is an unnecessary", "Reading config file",
    ];

    [GeneratedRegex(@"^Channel \d+: EXPERIMENTAL WAPR modem")]
    private static partial Regex WaprNotice();

    [GeneratedRegex(@"^Line \d+: Using a FIX_BITS value greater than")]
    private static partial Regex FixBitsNotice();

    [GeneratedRegex(@"(?i)unrecognized option|unknown option|invalid option|illegal option")]
    private static partial Regex BadOption();

    /// <summary>Parse the combined stdout/stderr of <c>direwolf -t 0 --check-config</c>.</summary>
    public static CheckConfigResult ParseOutput(string output, int? exitCode, IEnumerable<string>? secrets = null)
    {
        output = Redact(output, secrets);
        var lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool sawResult = lines.Any(l => l.StartsWith("check-config: result ", StringComparison.Ordinal));
        if (!sawResult)
        {
            bool unsupported = lines.Any(l => BadOption().IsMatch(l) && l.Contains("check-config", StringComparison.Ordinal))
                               || (lines.Any(l => BadOption().IsMatch(l)) && !lines.Any(l => l.StartsWith("check-config:", StringComparison.Ordinal)));
            return new CheckConfigResult
            {
                Status = unsupported ? CheckConfigStatus.Unsupported : CheckConfigStatus.Failed,
                RawOutput = output, ExitCode = exitCode,
                Message = unsupported ? UnsupportedMessage
                    : "direwolf --check-config ended without a result line (it may have stopped on a fatal configuration error).",
                Diagnostics = unsupported ? [] : ExtractDiagnostics(lines, out _),
            };
        }

        var diags = ExtractDiagnostics(lines, out var notes);
        var summary = ParseSummary(lines);
        return new CheckConfigResult
        {
            Status = diags.Count > 0 || (summary.DiagnosticCount ?? 0) > 0 || (exitCode ?? 0) != 0 ? CheckConfigStatus.Diagnostics : CheckConfigStatus.Ok,
            Diagnostics = diags, Notes = notes, Summary = summary, RawOutput = output, ExitCode = exitCode,
        };
    }

    private static List<ConfigDiagnostic> ExtractDiagnostics(string[] lines, out List<string> notes)
    {
        notes = [];
        var result = new List<ConfigDiagnostic>();
        int start = Array.FindIndex(lines, l => l.StartsWith("Reading config file", StringComparison.Ordinal));
        var msg = new StringBuilder();
        int? lineNo = null;
        void Flush()
        {
            if (msg.Length == 0) return;
            string m = msg.ToString();
            var sev = m.Contains("warning", StringComparison.OrdinalIgnoreCase) || m.Contains("REALLY BAD", StringComparison.Ordinal)
                      || m.Contains("should be", StringComparison.OrdinalIgnoreCase) || m.Contains("Are you sure", StringComparison.Ordinal)
                ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error;
            result.Add(new ConfigDiagnostic(sev, lineNo, "direwolf", m, DiagnosticSource.DireWolf));
            msg.Clear();
            lineNo = null;
        }
        for (int i = start < 0 ? 0 : start + 1; i < lines.Length; i++)
        {
            string l = lines[i].TrimEnd();
            if (l.StartsWith("check-config:", StringComparison.Ordinal)) break;
            if (l.Length == 0) { Flush(); continue; }
            if (IgnoredPrefixes.Any(p => l.StartsWith(p, StringComparison.Ordinal))) continue;
            if (WaprNotice().IsMatch(l) || FixBitsNotice().IsMatch(l)) { Flush(); notes.Add(l); continue; }
            var m = LineNumber().Match(l);
            bool startsNew = msg.Length == 0 || m.Success || l.StartsWith("Config file", StringComparison.Ordinal)
                             || l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase);
            if (startsNew)
            {
                // Continuation lines of the same message repeat the line number ("Line 7: ... Line 7: ...").
                if (msg.Length > 0 && m.Success && lineNo == int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) && !l.StartsWith("Config file", StringComparison.Ordinal))
                {
                    msg.Append('\n').Append(l);
                    continue;
                }
                Flush();
                msg.Append(l);
                if (m.Success) lineNo = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else msg.Append('\n').Append(l);
        }
        Flush();
        return result;
    }

    [GeneratedRegex(@"^check-config: adevice (\d+) in ""(.*)"" out ""(.*)"" rate (-?\d+) channels (-?\d+)$")]
    private static partial Regex AdeviceRe();

    [GeneratedRegex(@"^check-config: channel (\d+) radio mycall (\S*) modem (\S+) baud (-?\d+) mark (-?\d+) space (-?\d+) profiles ""(.*)"" ptt (\S+)(.*)$")]
    private static partial Regex RadioRe();

    [GeneratedRegex(@"^check-config: channel (\d+) (igate|nettnc) mycall ?(\S*)$")]
    private static partial Regex VirtualRe();

    [GeneratedRegex(@"^check-config: igate server ""(.*)"" port (-?\d+) login (\S*) txchan (-?\d+)")]
    private static partial Regex IGateRe();

    [GeneratedRegex(@"^check-config: beacon (\S+) sendto (\S+) chan (-?\d+) line (\d+)$")]
    private static partial Regex BeaconRe();

    [GeneratedRegex(@"^check-config: waprgate (-?\d+) (-?\d+) types 0x([0-9a-fA-F]+)$")]
    private static partial Regex WaprGateRe();

    [GeneratedRegex(@"^check-config: serialkiss ""(.*)"" speed (-?\d+)$")]
    private static partial Regex SerialKissRe();

    private static int I(string s) => int.Parse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    /// <summary>Parse the "check-config: ..." lines.</summary>
    public static CheckConfigSummary ParseSummary(IEnumerable<string> lines)
    {
        var s = new CheckConfigSummary();
        foreach (var raw in lines)
        {
            string l = raw.TrimEnd();
            if (!l.StartsWith("check-config: ", StringComparison.Ordinal)) continue;
            string rest = l["check-config: ".Length..];
            var w = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Match m;
            switch (w.Length > 0 ? w[0] : "")
            {
                case "version": s.Version = w.Length > 1 ? w[1] : null; break;
                case "features": foreach (var f in w.Skip(1)) s.Features.Add(f); break;
                case "adevice" when (m = AdeviceRe().Match(l)).Success:
                    s.AudioDevices.Add(new CheckAudioDevice(I(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value, I(m.Groups[4].Value), I(m.Groups[5].Value)));
                    break;
                case "channel" when (m = RadioRe().Match(l)).Success:
                {
                    var extra = m.Groups[9].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int? fx = null, il = null; string? wp = null; double? air = null;
                    for (int i = 0; i + 1 < extra.Length; i++)
                    {
                        switch (extra[i])
                        {
                            case "fx25tx": fx = I(extra[++i]); break;
                            case "il2ptx": il = I(extra[++i]); break;
                            case "wapr": wp = extra[++i]; break;
                            case "airtime": air = double.Parse(extra[++i], CultureInfo.InvariantCulture); break;
                        }
                    }
                    s.Channels.Add(new CheckChannel(I(m.Groups[1].Value), ChannelMedium.Radio, m.Groups[2].Value, m.Groups[3].Value,
                        I(m.Groups[4].Value), I(m.Groups[5].Value), I(m.Groups[6].Value), m.Groups[7].Value, m.Groups[8].Value, fx, il, wp, air));
                    break;
                }
                case "channel" when (m = VirtualRe().Match(l)).Success:
                    s.Channels.Add(new CheckChannel(I(m.Groups[1].Value), m.Groups[2].Value == "igate" ? ChannelMedium.IGate : ChannelMedium.NetTnc, m.Groups[3].Value));
                    break;
                case "agwport" when w.Length > 1: s.AgwPort = I(w[1]); break;
                case "tcpbind" when w.Length > 1:
                    s.TcpBindReported = true;
                    s.BindLocalOnly = string.Equals(w[1], "local", StringComparison.OrdinalIgnoreCase);
                    break;
                case "kissport" when w.Length > 3: s.KissPorts.Add(new CheckKissPort(I(w[1]), I(w[3]))); break;
                case "serialkiss" when (m = SerialKissRe().Match(l)).Success: s.SerialKiss = new CheckSerialKiss(m.Groups[1].Value, I(m.Groups[2].Value)); break;
                case "digipeat" when w.Length > 2: s.Digipeat.Add(new CheckChannelPair(I(w[1]), I(w[2]))); break;
                case "regen" when w.Length > 2: s.Regen.Add(new CheckChannelPair(I(w[1]), I(w[2]))); break;
                case "cdigipeat" when w.Length > 2: s.CDigipeat.Add(new CheckChannelPair(I(w[1]), I(w[2]))); break;
                case "igate" when (m = IGateRe().Match(l)).Success:
                    s.IGate = new CheckIGate(m.Groups[1].Value, I(m.Groups[2].Value), m.Groups[3].Value, I(m.Groups[4].Value));
                    break;
                case "beacon" when (m = BeaconRe().Match(l)).Success:
                    s.Beacons.Add(new CheckBeacon(m.Groups[1].Value, m.Groups[2].Value, I(m.Groups[3].Value), I(m.Groups[4].Value)));
                    break;
                case "waprgate" when (m = WaprGateRe().Match(l)).Success:
                    s.WaprGates.Add(new CheckWaprGate(I(m.Groups[1].Value), I(m.Groups[2].Value), int.Parse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                    break;
                case "result" when w.Length > 1: s.DiagnosticCount = I(w[1]); break;
                default: s.UnknownLines.Add(l); break;
            }
        }
        return s;
    }
}
