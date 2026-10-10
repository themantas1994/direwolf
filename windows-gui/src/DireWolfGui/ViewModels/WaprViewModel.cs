using System.Collections.ObjectModel;
using System.Globalization;
using DireWolfGui.Core.Config;
using DireWolfGui.Core.Wapr;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>What a Dire Wolf output line says about WAPR (from the messages in src/wapr_*.c).</summary>
public enum WaprLogKind { Other, Acknowledged, Resent, GaveUp, AirtimeLimit, NotSent, DecoderOverrun, NotGated, Gateway, Suppressed }

public static class WaprLogClassifier
{
    /// <summary>True for lines that belong in the WAPR diagnostics list.</summary>
    public static bool IsWaprLine(string line) =>
        line.Contains("WAPR channel", StringComparison.Ordinal) || line.Contains("[WAPR gate", StringComparison.Ordinal) ||
        line.Contains(" WAPR ", StringComparison.Ordinal) || line.StartsWith("WAPR", StringComparison.Ordinal) ||
        line.Contains("WAPRGATE", StringComparison.Ordinal);

    public static WaprLogKind Classify(string line)
    {
        if (line.Contains("[WAPR gate", StringComparison.Ordinal) && line.Contains("not gated", StringComparison.Ordinal)) return WaprLogKind.NotGated;
        if (line.Contains("WAPR gateway", StringComparison.Ordinal)) return WaprLogKind.Gateway;
        if (line.Contains("decoder can't keep up", StringComparison.Ordinal) || line.Contains("decoder can’t keep up", StringComparison.Ordinal)) return WaprLogKind.DecoderOverrun;
        if (line.Contains("not sent, airtime limit", StringComparison.Ordinal)) return WaprLogKind.AirtimeLimit;
        if (line.Contains("not sent,", StringComparison.Ordinal)) return WaprLogKind.NotSent;
        if (line.Contains("gave up", StringComparison.Ordinal)) return WaprLogKind.GaveUp;
        if (line.Contains("sending it again", StringComparison.Ordinal)) return WaprLogKind.Resent;
        if (line.Contains("suppressed", StringComparison.Ordinal)) return WaprLogKind.Suppressed;
        if (line.Contains(" acknowledged frame ", StringComparison.Ordinal)) return WaprLogKind.Acknowledged;
        return WaprLogKind.Other;
    }

    public static string Brush(WaprLogKind k) => k switch
    {
        WaprLogKind.Acknowledged => "Success",
        WaprLogKind.Resent or WaprLogKind.NotGated or WaprLogKind.Suppressed => "Warning",
        WaprLogKind.GaveUp or WaprLogKind.AirtimeLimit or WaprLogKind.NotSent or WaprLogKind.DecoderOverrun => "Error",
        WaprLogKind.Gateway => "Gateway",
        _ => "Neutral",
    };
}

public sealed record WaprLogLine(DateTimeOffset Time, WaprLogKind Kind, string Brush, string Text);

public sealed class WaprProfileRow(WaprProfile p)
{
    public WaprProfile Profile { get; } = p;
    public string Name => Profile.Name;
    public string Use => Profile.Use;
    public string SymbolRate => $"{Profile.SymbolRate:0} Bd";
    public string Tones => Profile.ToneDescription;
    public string FrameTime => $"{Profile.FrameSeconds:0.##} s";
    public string WithDefaults => $"{WaprSupport.SecondsPerFrame(Profile):0.##} s";
}

/// <summary>A radio channel offered for WAPR setup.</summary>
public sealed record WaprChannelChoice(int Number, string Description, bool HasModemLine, bool IsWapr, int? ModemLine, string ModemText)
{
    public override string ToString() => Description;
}

/// <summary>A WAPRGATE type checkbox.</summary>
public sealed class GateTypeOption(string name, Action changed) : ObservableObject
{
    public string Name { get; } = name;
    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { if (Set(ref _isChecked, value)) changed(); } }
}

/// <summary>
/// Experimental WAPR: capability of the selected Dire Wolf build, profiles and airtime arithmetic,
/// explicit per-channel setup, WAPRGATE rules and WAPR lines from Dire Wolf's output.
/// Nothing is converted to WAPR without the operator's explicit action and confirmation.
/// </summary>
public sealed class WaprViewModel : PageViewModel
{
    private const int MaxLines = 500;
    private readonly IShell _shell;
    private int _builtVersion = -1;

    public WaprViewModel(IShell shell) : base("WAPR (experimental)", "\uE945")
    {
        _shell = shell;
        Session = ConfigurationSession.For(shell);
        Session.DocumentChanged += (_, _) => { if (IsActive) Rebuild(); };
        _shell.ConsoleLine += OnConsoleLine;
        _shell.StationStateChanged += (_, _) => { if (IsActive) UpdateCapability(); };

        Profiles = WaprSupport.Profiles.Select(p => new WaprProfileRow(p)).ToList();
        ProfileNames = WaprSupport.Profiles.Select(p => p.Name).ToList();
        GateTypes = WaprSupport.GateTypes.Keys.Where(k => k != "ALL").Select(k => new GateTypeOption(k, () => OnPropertyChanged(nameof(GateValidation)))).ToList();

        CheckNowCommand = new AsyncCommand(CheckNowAsync, () => !string.IsNullOrWhiteSpace(_shell.ConfigPath) && !string.IsNullOrWhiteSpace(_shell.DireWolfPath));
        SaveCommand = new AsyncCommand(async () => { await Session.SaveAsync(); }, () => Session.IsDirty);
        RevertCommand = new AsyncCommand(async () => { if (Dialogs.Confirm("Discard all unsaved changes to the configuration?")) await Session.RevertAsync(); }, () => Session.IsDirty || Session.IsStale);
        ApplyChannelCommand = new RelayCommand(ApplyChannel, () => IsSupported && SelectedChannel != null);
        RevertChannelCommand = new RelayCommand(RevertChannel, () => IsSupported && SelectedChannel is { IsWapr: true });
        AddGateCommand = new RelayCommand(AddGate, () => IsSupported);
        DisableGateCommand = new RelayCommand(p => DisableGate(p as ServiceLineRow), p => p is ServiceLineRow { IsDisabled: false });
        EnableGateCommand = new RelayCommand(p => EnableGate(p as ServiceLineRow), p => IsSupported && p is ServiceLineRow { IsDisabled: true });
        ResetCountersCommand = new RelayCommand(ResetCounters);
        Recalculate();
    }

    public ConfigurationSession Session { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand RevertCommand { get; }

    public string ExperimentalNotice => WaprSupport.ExperimentalNotice;

    public string VerificationNote =>
        "Dire Wolf Station does not verify WAPR on real hardware or on the air. The airtime figures below are arithmetic from the profile " +
        "definitions (doc/wapr/USAGE.md); WAPR's measured performance so far comes from simulations only.";

    protected override async void OnShown()
    {
        try
        {
            UpdateCapability();
            await Session.EnsureCurrentAsync();
            _builtVersion = -1;
            Rebuild();
        }
        catch (Exception e)
        {
            _shell.SetStatus("WAPR page: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ capability

    private bool _isSupported;
    public bool IsSupported { get => _isSupported; private set { if (Set(ref _isSupported, value)) OnPropertyChanged(nameof(IsNotSupported)); } }
    public bool IsNotSupported => !IsSupported;

    private string _capabilityText = "";
    public string CapabilityText { get => _capabilityText; private set => Set(ref _capabilityText, value); }

    private string _capabilityKind = "Neutral";
    public string CapabilityKind { get => _capabilityKind; private set => Set(ref _capabilityKind, value); }

    public AsyncCommand CheckNowCommand { get; }

    private void UpdateCapability()
    {
        var r = _shell.LastCheck;
        if (r == null)
        {
            IsSupported = false;
            CapabilityText = "Not checked yet, so it is unknown whether this Dire Wolf build includes WAPR. Use \"Check now\" (runs direwolf --check-config, which opens no audio, PTT or network). " + WaprSupport.NotSupportedExplanation;
            CapabilityKind = "Warning";
        }
        else if (r.Status == CheckConfigStatus.Unsupported)
        {
            IsSupported = false;
            CapabilityText = "The real-parser check is unavailable for this Dire Wolf build (no --check-config option), so WAPR support cannot be confirmed and the WAPR controls stay disabled. " + WaprSupport.NotSupportedExplanation;
            CapabilityKind = "Warning";
        }
        else if (r.Summary == null)
        {
            IsSupported = false;
            CapabilityText = "The check did not produce a result" + (r.Message != null ? ": " + r.Message : ".") + " WAPR support is unknown, so the WAPR controls stay disabled.";
            CapabilityKind = "Error";
        }
        else
        {
            IsSupported = WaprSupport.IsSupported(r.Summary, out var expl);
            CapabilityText = expl + (r.Summary.Version != null ? $" (Dire Wolf {r.Summary.Version})" : "");
            CapabilityKind = IsSupported ? "Success" : "Warning";
        }
    }

    private async Task CheckNowAsync()
    {
        CapabilityText = "Checking…";
        await _shell.CheckConfigAsync();
        UpdateCapability();
    }

    // ------------------------------------------------------------------ profiles and calculator

    public IReadOnlyList<WaprProfileRow> Profiles { get; }
    public IReadOnlyList<string> ProfileNames { get; }

    private string _calcProfile = "H150", _framesPerHour = "6", _txDelay = "30", _txTail = "10", _airtimeLimit = "", _payload = "";
    public string CalcProfile { get => _calcProfile; set { if (Set(ref _calcProfile, value ?? "H150")) Recalculate(); } }
    public string FramesPerHour { get => _framesPerHour; set { if (Set(ref _framesPerHour, value ?? "")) Recalculate(); } }
    public string TxDelay { get => _txDelay; set { if (Set(ref _txDelay, value ?? "")) Recalculate(); } }
    public string TxTail { get => _txTail; set { if (Set(ref _txTail, value ?? "")) Recalculate(); } }
    public string AirtimeLimit { get => _airtimeLimit; set { if (Set(ref _airtimeLimit, value ?? "")) Recalculate(); } }

    private string _dutyText = "", _dutyKind = "Neutral", _windowText = "";
    public string DutyText { get => _dutyText; private set => Set(ref _dutyText, value); }
    public string DutyKind { get => _dutyKind; private set => Set(ref _dutyKind, value); }
    public string WindowText { get => _windowText; private set => Set(ref _windowText, value); }

    private static double? ParseNumber(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v
        : double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v) ? v : null;

    private void Recalculate()
    {
        var p = WaprSupport.FindProfile(CalcProfile);
        double? fph = ParseNumber(FramesPerHour);
        bool tdOk = ConfigFacts.TryInt(TxDelay.Trim(), out int td) && td <= 255;
        bool ttOk = ConfigFacts.TryInt(TxTail.Trim(), out int tt) && tt <= 255;
        double? lim = AirtimeLimit.Trim().Length == 0 ? null : ParseNumber(AirtimeLimit);
        if (p == null || fph is not double f || f < 0 || !tdOk || !ttOk || (AirtimeLimit.Trim().Length > 0 && lim is not (> 0 and <= 100)))
        {
            DutyText = "Enter frames per hour (a number), TXDELAY and TXTAIL in 10 ms units (0-255) and, optionally, an AIRTIME limit from 0 to 100 %.";
            DutyKind = "Neutral";
            WindowText = "";
            return;
        }
        var est = WaprSupport.EstimateDuty(p, f, td, tt, lim);
        DutyText = est.Explanation;
        DutyKind = est.Level switch { DutyLevel.Ok => "Success", DutyLevel.Elevated => "Warning", DutyLevel.High => "Warning", _ => "Error" };
        WindowText = lim is double l
            ? $"With AIRTIME={l:0.##}: at most {WaprSupport.FramesPerAirtimeWindow(p, l, td, tt)} frames per 10 minutes (acknowledgements are always sent and also use airtime)."
            : "No AIRTIME limit: Dire Wolf does not cap this channel's share of the time.";
    }

    public string Payload
    {
        get => _payload;
        set { if (Set(ref _payload, value ?? "")) OnPropertyChanged(nameof(PayloadText)); }
    }

    public string PayloadText => WaprSupport.FitsPayload(Payload, out int bytes)
        ? $"{bytes} of {WaprSupport.MaxPayloadBytes} bytes: fits one WAPR frame."
        : $"{bytes} bytes: too long. A WAPR frame carries at most {WaprSupport.MaxPayloadBytes} bytes of information; longer frames are refused (nothing falls back to AX.25).";

    // ------------------------------------------------------------------ per-channel setup

    private List<WaprChannelChoice> _channels = [];
    public List<WaprChannelChoice> Channels { get => _channels; private set => Set(ref _channels, value); }

    private WaprChannelChoice? _selectedChannel;
    public WaprChannelChoice? SelectedChannel { get => _selectedChannel; set => Set(ref _selectedChannel, value); }

    private string _setupProfile = "H150", _setupAirtime = "";
    public string SetupProfile { get => _setupProfile; set => Set(ref _setupProfile, value ?? "H150"); }
    public string SetupAirtime { get => _setupAirtime; set => Set(ref _setupAirtime, value ?? ""); }

    public RelayCommand ApplyChannelCommand { get; }
    public RelayCommand RevertChannelCommand { get; }

    public bool HasDocument => Session.HasDocument;
    public string NoDocumentText => Session.LoadError ?? "No configuration file is selected: open one on the Configuration page to set up WAPR channels and gateway rules.";

    private void Rebuild()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(NoDocumentText));
        var doc = Session.Document;
        if (doc == null)
        {
            Channels = [];
            GateRows.Clear();
            GateChannelChoices = [];
            return;
        }
        if (_builtVersion == Session.Version) return;
        _builtVersion = Session.Version;
        var facts = ConfigFacts.From(doc);
        int? keep = SelectedChannel?.Number;
        Channels = facts.RadioChannels.Select(n =>
        {
            var c = facts.Channel(n);
            var line = doc.FindDirective("MODEM", n);
            string modem = line == null ? "" : "MODEM " + ConfigEdit.JoinArgs(line.Arguments);
            string desc = c.IsWapr ? $"Channel {n}: WAPR {c.WaprProfile}{(c.WaprAirtime is double a ? $" AIRTIME={a:0.##}" : "")} (line {line?.LineNumber})"
                : line == null ? $"Channel {n}: no MODEM line (Dire Wolf's default 1200 bps AX.25)"
                : $"Channel {n}: {modem} (AX.25, line {line.LineNumber})";
            return new WaprChannelChoice(n, desc, line != null, c.IsWapr, line?.LineNumber, modem);
        }).ToList();
        SelectedChannel = Channels.FirstOrDefault(c => c.Number == keep) ?? Channels.FirstOrDefault(c => !c.HasModemLine) ?? Channels.FirstOrDefault();
        GateRows.ReplaceWith(ConfigLineQuery.Find(doc, "WAPRGATE"));
        GateChannelChoices = facts.RadioChannels.Select(IntervalText.Number).ToList();
        GateToChoices = [.. GateChannelChoices, "IS"];
        OnPropertyChanged(nameof(GateValidation));
    }

    private void ApplyChannel()
    {
        if (SelectedChannel is not { } ch || Session.Document == null) return;
        if (!WaprSupport.IsValidProfile(SetupProfile)) { Dialogs.Warning("Choose a WAPR profile (F600, H150 or R25)."); return; }
        string? airtime = null;
        if (SetupAirtime.Trim().Length > 0)
        {
            string token = "AIRTIME=" + SetupAirtime.Trim();
            if (!WaprSupport.TryParseAirtime(token, out double pct)) { Dialogs.Warning("AIRTIME is a percentage greater than 0 and at most 100, e.g. 10 (use a dot for decimals)."); return; }
            airtime = "AIRTIME=" + pct.ToString("0.##", CultureInfo.InvariantCulture);
        }
        if (ch.HasModemLine && !ch.IsWapr &&
            !Dialogs.Confirm($"Channel {ch.Number} is configured as an AX.25 channel ({ch.ModemText}, line {ch.ModemLine}).\n\n" +
                             "Replace that MODEM line with WAPR? The whole channel becomes WAPR: it no longer receives or transmits AX.25/APRS, " +
                             "and digipeater, IGate and client traffic on this channel follow the WAPR rules (32-byte information part, no digipeater path).",
                             "Replace the channel's modem", warning: true))
            return;
        var p = WaprSupport.FindProfile(SetupProfile)!;
        if (!OperatorNotice.ConfirmTransmit($"Make channel {ch.Number} an experimental WAPR channel ({p.Name}: {p.Use}, {p.SymbolRate:0} Bd, {p.ToneDescription}{(airtime != null ? ", " + airtime : "")})?",
                WaprSupport.ExperimentalNotice + "\n\nAnything that transmits on this channel (client applications, beacons, gateway rules) will then transmit WAPR."))
            return;
        var args = airtime == null ? new[] { "WAPR", p.Name } : ["WAPR", p.Name, airtime];
        int n = ch.Number;
        Session.Edit(d => d.SetDirective("MODEM", args, n), $"Channel {n} set to WAPR {p.Name}.");
    }

    private void RevertChannel()
    {
        if (SelectedChannel is not { IsWapr: true } ch) return;
        if (!Dialogs.Confirm($"Change channel {ch.Number} back to AX.25 (MODEM 1200)?\n\nWAPRGATE rules that use this channel stop working (the validator will point them out).", "Leave WAPR")) return;
        int n = ch.Number;
        Session.Edit(d => d.SetDirective("MODEM", ["1200"], n), $"Channel {n} set back to MODEM 1200.");
    }

    // ------------------------------------------------------------------ WAPRGATE

    public ObservableCollection<ServiceLineRow> GateRows { get; } = [];
    public IReadOnlyList<GateTypeOption> GateTypes { get; }

    private List<string> _gateChannelChoices = [];
    public List<string> GateChannelChoices { get => _gateChannelChoices; private set => Set(ref _gateChannelChoices, value); }
    private List<string> _gateToChoices = ["IS"];
    public List<string> GateToChoices { get => _gateToChoices; private set => Set(ref _gateToChoices, value); }

    private string _gateFrom = "", _gateTo = "";
    public string GateFrom { get => _gateFrom; set { if (Set(ref _gateFrom, value ?? "")) OnPropertyChanged(nameof(GateValidation)); } }
    public string GateTo { get => _gateTo; set { if (Set(ref _gateTo, value ?? "")) OnPropertyChanged(nameof(GateValidation)); } }

    private bool _gateAll = true;
    public bool GateAll { get => _gateAll; set { if (Set(ref _gateAll, value)) OnPropertyChanged(nameof(GateValidation)); } }

    public RelayCommand AddGateCommand { get; }
    public RelayCommand DisableGateCommand { get; }
    public RelayCommand EnableGateCommand { get; }

    private string TypesToken => GateAll ? "ALL" : string.Join(",", GateTypes.Where(t => t.IsChecked).Select(t => t.Name));

    /// <summary>Problems with the rule being entered, or the plain-language effect when it is valid.</summary>
    public string GateValidation => ValidateGate(out _) ?? DescribeGate();

    private string? ValidateGate(out int bits)
    {
        bits = 0;
        var doc = Session.Document;
        if (doc == null) return "No configuration loaded.";
        var f = ConfigFacts.From(doc);
        if (!ConfigFacts.TryInt(GateFrom.Trim(), out int from) || !f.IsRadioChannel(from)) return "From: choose a configured radio channel (IS cannot be the source: IS to WAPR is not supported).";
        bool toIs = GateTo.Trim().Equals("IS", StringComparison.OrdinalIgnoreCase);
        int to = -1;
        if (!toIs && (!ConfigFacts.TryInt(GateTo.Trim(), out to) || !f.IsRadioChannel(to))) return "To: choose a configured radio channel or IS (APRS-IS).";
        if (!toIs && to == from) return "From and to must be different channels.";
        bool fromWapr = f.Channel(from).IsWapr, toWapr = !toIs && f.Channel(to).IsWapr;
        if (!fromWapr && !toWapr) return "One side of a WAPRGATE rule must be a WAPR channel. Set a channel up as WAPR above first.";
        if (toIs && !f.HasIGate) return "Gating to APRS-IS needs the IGate (IGSERVER and IGLOGIN), configured on the Gateway & digipeater page.";
        bits = WaprSupport.ParseGateTypes(TypesToken);
        if (!GateAll && bits == 0) return "Tick at least one type, or ALL.";
        return null;
    }

    private string DescribeGate()
    {
        bool toIs = GateTo.Trim().Equals("IS", StringComparison.OrdinalIgnoreCase);
        return $"WAPRGATE {GateFrom.Trim()} {GateTo.Trim().ToUpperInvariant()} {TypesToken}: forwards {WaprSupport.DescribeGateTypes(WaprSupport.ParseGateTypes(TypesToken))} frames heard on channel {GateFrom.Trim()} " +
               (toIs ? "to APRS-IS through the IGate." : $"by TRANSMITTING them on channel {GateTo.Trim()}.") +
               " Relayed frames are marked so no gateway passes them on again; duplicates within 60 s are dropped.";
    }

    private void AddGate()
    {
        if (ValidateGate(out _) is { } err) { Dialogs.Warning(err); return; }
        bool toIs = GateTo.Trim().Equals("IS", StringComparison.OrdinalIgnoreCase);
        string body = $"WAPRGATE {GateFrom.Trim()} {(toIs ? "IS" : GateTo.Trim())} {TypesToken}";
        if (!OperatorNotice.ConfirmTransmit($"Add the experimental gateway rule:\n{body}", DescribeGate() + "\n\n" + WaprSupport.ExperimentalNotice)) return;
        Session.Edit(d =>
        {
            var last = d.FindDirectives("WAPRGATE").LastOrDefault();
            if (last == null) d.SetDirective("WAPRGATE", [GateFrom.Trim(), toIs ? "IS" : GateTo.Trim(), TypesToken]);
            else d.InsertLine(last.Index + 1, body);
        }, "WAPRGATE rule added.");
    }

    private void DisableGate(ServiceLineRow? row)
    {
        if (row == null) return;
        if (!Dialogs.Confirm($"Disable line {row.LineNumber}?\n\n{row.Text}\n\nIt is commented out (not deleted).")) return;
        Session.Edit(d => d.DisableDirective(row.Index, "disabled on the WAPR page"), $"WAPRGATE on line {row.LineNumber} disabled.");
    }

    private void EnableGate(ServiceLineRow? row)
    {
        if (row == null) return;
        if (!OperatorNotice.ConfirmTransmit($"Enable the experimental gateway rule on line {row.LineNumber}:\n{row.Text}", WaprSupport.ExperimentalNotice)) return;
        Session.Edit(d =>
        {
            if (!d.EnableDirective(row.Index)) throw new InvalidOperationException("This line was not disabled by Dire Wolf Station.");
        }, $"WAPRGATE on line {row.LineNumber} enabled.");
    }

    // ------------------------------------------------------------------ runtime log

    public ObservableCollection<WaprLogLine> LogLines { get; } = [];

    private int _acked, _resent, _gaveUp, _airtime, _notSent, _overruns, _notGated;
    public int Acknowledged { get => _acked; private set => Set(ref _acked, value); }
    public int Resent { get => _resent; private set => Set(ref _resent, value); }
    public int GaveUp { get => _gaveUp; private set => Set(ref _gaveUp, value); }
    public int AirtimeRefused { get => _airtime; private set => Set(ref _airtime, value); }
    public int NotSent { get => _notSent; private set => Set(ref _notSent, value); }
    public int DecoderOverruns { get => _overruns; private set => Set(ref _overruns, value); }
    public int NotGated { get => _notGated; private set => Set(ref _notGated, value); }

    public RelayCommand ResetCountersCommand { get; }

    private void OnConsoleLine(object? sender, string line)
    {
        if (!WaprLogClassifier.IsWaprLine(line)) return;
        var kind = WaprLogClassifier.Classify(line);
        switch (kind)
        {
            case WaprLogKind.Acknowledged: Acknowledged++; break;
            case WaprLogKind.Resent: Resent++; break;
            case WaprLogKind.GaveUp: GaveUp++; break;
            case WaprLogKind.AirtimeLimit: AirtimeRefused++; break;
            case WaprLogKind.NotSent: NotSent++; break;
            case WaprLogKind.DecoderOverrun: DecoderOverruns++; break;
            case WaprLogKind.NotGated: NotGated++; break;
        }
        LogLines.Insert(0, new WaprLogLine(DateTimeOffset.Now, kind, WaprLogClassifier.Brush(kind), line.Trim()));
        while (LogLines.Count > MaxLines) LogLines.RemoveAt(LogLines.Count - 1);
    }

    private void ResetCounters()
    {
        Acknowledged = Resent = GaveUp = AirtimeRefused = NotSent = DecoderOverruns = NotGated = 0;
        LogLines.Clear();
    }
}
