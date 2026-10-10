using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using DireWolfGui.Core.Config;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>A service from <see cref="StationServicesAnalyzer"/> for display.</summary>
public sealed class ServiceCard(StationService s)
{
    public StationService Service { get; } = s;
    public string Title => Service.Title;
    public string Explanation => Service.Explanation;
    public string Badge => Service.Transmits && Service.Forwards ? "Transmits and forwards" : Service.Transmits ? "Can transmit" : Service.Forwards ? "Forwards" : "Information";
    public string BadgeKind => Service.Transmits ? "Tx" : Service.Forwards ? "IgateColor" : "Neutral";
    public string LineText => Service.Line is int l ? $"Line {l}" : "";
}

/// <summary>
/// Gateway &amp; digipeater: what the configuration makes the station transmit or forward, editors for
/// DIGIPEAT, the IGate, beacons and filters (each change confirmed, then saved with a diff preview),
/// and a live list of forwarding lines from Dire Wolf's output.
/// </summary>
public sealed class ServicesViewModel : PageViewModel
{
    private const int MaxEvents = 300;
    private readonly IShell _shell;
    private int _builtVersion = -1;

    public ServicesViewModel(IShell shell) : base("Gateway & digipeater", "\uE909", "Ctrl+8")
    {
        _shell = shell;
        Session = ConfigurationSession.For(shell);
        Session.DocumentChanged += (_, _) => { if (IsActive) Rebuild(); };
        _shell.ConsoleLine += OnConsoleLine;

        SaveCommand = new AsyncCommand(async () => { await Session.SaveAsync(); }, () => Session.IsDirty);
        RevertCommand = new AsyncCommand(async () => { if (Dialogs.Confirm("Discard all unsaved changes to the configuration?")) await Session.RevertAsync(); }, () => Session.IsDirty || Session.IsStale);
        OpenConfigurationCommand = new RelayCommand(() => _shell.Navigate<ConfigurationViewModel>());

        PresetStandardCommand = new RelayCommand(() => { DigiAlias = @"^WIDE[3-7]-[1-7]$|^TEST$"; DigiWide = @"^WIDE[12]-[12]$"; DigiOption = "TRACE"; PresetNote = StandardPresetText; });
        PresetFillInCommand = new RelayCommand(() => { DigiAlias = @"^WIDE1-1$"; DigiWide = @"^WIDE1-1$"; DigiOption = ""; PresetNote = FillInPresetText; });
        NewDigiCommand = new RelayCommand(() => { EditingDigi = null; DigiFrom = DigiTo = "0"; DigiAlias = DigiWide = DigiOption = ""; PresetNote = ""; });
        EditDigiCommand = new RelayCommand(p => LoadDigi(p as ServiceLineRow), p => p is ServiceLineRow);
        SaveDigiCommand = new RelayCommand(SaveDigi);
        DisableRowCommand = new RelayCommand(p => DisableRow(p as ServiceLineRow), p => p is ServiceLineRow { IsDisabled: false });
        EnableRowCommand = new RelayCommand(p => EnableRow(p as ServiceLineRow), p => p is ServiceLineRow { IsDisabled: true });

        DisableIGateCommand = new RelayCommand(DisableIGate, () => IgateConfigured);
        ApplyTxViaCommand = new RelayCommand(ApplyTxVia);
        DisableTxViaCommand = new RelayCommand(() => DisableKeyword("IGTXVIA", "Internet-to-radio transmitting"), () => TxViaConfigured);
        ApplyIgFilterCommand = new RelayCommand(ApplyIgFilter);
        ApplyIgTxLimitCommand = new RelayCommand(ApplyIgTxLimit);

        NewBeaconCommand = new RelayCommand(NewBeacon);
        EditBeaconCommand = new RelayCommand(p => LoadBeacon(p as BeaconRow), p => p is BeaconRow);
        SaveBeaconCommand = new RelayCommand(SaveBeacon);
        DisableBeaconCommand = new RelayCommand(p => DisableRow((p as BeaconRow)?.Row), p => p is BeaconRow { Row.IsDisabled: false });
        EnableBeaconCommand = new RelayCommand(p => EnableRow((p as BeaconRow)?.Row), p => p is BeaconRow { Row.IsDisabled: true });

        ClearEventsCommand = new RelayCommand(() => Events.Clear());
        NewBeacon();
    }

    public ConfigurationSession Session { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand RevertCommand { get; }
    public RelayCommand OpenConfigurationCommand { get; }

    public bool HasDocument => Session.HasDocument;
    public bool NoDocument => !Session.HasDocument;
    public string NoDocumentText => Session.LoadError ?? "No configuration file is selected. Open one on the Configuration page or create one with the Setup wizard.";

    protected override async void OnShown()
    {
        try
        {
            await Session.EnsureCurrentAsync();
            _builtVersion = -1;
            Rebuild();
        }
        catch (Exception e)
        {
            _shell.SetStatus("Gateway & digipeater page: " + e.Message);
        }
    }

    private void Rebuild()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(NoDocument));
        OnPropertyChanged(nameof(NoDocumentText));
        var doc = Session.Document;
        if (doc == null)
        {
            Services.Clear(); Warnings.Clear(); DigiRows.Clear(); Beacons.Clear(); FilterRows.Clear();
            ReportSummary = "";
            return;
        }
        if (_builtVersion == Session.Version) return;
        _builtVersion = Session.Version;

        var summary = Session.IsDirty ? null : _shell.LastCheck?.Summary;
        var report = StationServicesAnalyzer.Analyze(doc, summary);
        Services.ReplaceWith(report.Services.Select(s => new ServiceCard(s)));
        Warnings.ReplaceWith(report.Warnings.Select(w => new DiagnosticRow(w)));
        ReportSummary = !report.TransmitsAnything && !report.ForwardsAnything
            ? "Nothing in this configuration transmits or forwards traffic on its own, and no client port is open."
            : $"{report.Services.Count(s => s.Transmits)} item(s) can make the station transmit, {report.Services.Count(s => s.Forwards)} forward traffic." +
              (Session.IsDirty ? " (Includes your unsaved changes.)" : "");

        var facts = ConfigFacts.From(doc);
        RadioChannels = facts.RadioChannels.Select(IntervalText.Number).ToList();
        DigiRows.ReplaceWith(ConfigLineQuery.Find(doc, "DIGIPEAT"));
        FilterRows.ReplaceWith(ConfigLineQuery.Find(doc, "FILTER", "CFILTER"));
        Beacons.ReplaceWith(ConfigLineQuery.Find(doc, BeaconOptions.Kinds).Select(r => new BeaconRow(r, facts)));

        // IGate
        var server = doc.FindDirective("IGSERVER") ?? doc.FindDirective("CWOPSERVER");
        var login = doc.FindDirective("IGLOGIN");
        var txvia = doc.FindDirective("IGTXVIA");
        IgateConfigured = server != null || login != null;
        IgServer = server == null ? "noam.aprs2.net" : ConfigEdit.JoinArgs(server.Arguments);
        IgLoginCall = login?.Arguments.FirstOrDefault() ?? facts.Channel(0).MyCall ?? "";
        PasscodeState = login == null ? "No IGLOGIN line." : login.Arguments.Count > 1 ? "A passcode is set in the configuration file (not shown)." : "IGLOGIN has no passcode.";
        TxViaConfigured = txvia != null;
        IgTxChannel = txvia?.Arguments.FirstOrDefault() ?? RadioChannels.FirstOrDefault() ?? "0";
        IgTxVia = txvia != null && txvia.Arguments.Count > 1 ? string.Join(" ", txvia.Arguments.Skip(1)) : "";
        IgFilter = doc.FindDirective("IGFILTER") is { } f ? ConfigEdit.JoinArgs(f.Arguments) : "";
        var lim = doc.FindDirective("IGTXLIMIT");
        IgTxLimit1 = lim?.Arguments.ElementAtOrDefault(0) ?? "";
        IgTxLimit5 = lim?.Arguments.ElementAtOrDefault(1) ?? "";
        IgateState = !IgateConfigured ? "The IGate is not configured: nothing is sent to APRS-IS."
            : $"IGate {(server != null ? "to " + IgServer : "(no IGSERVER line)")}, login {IgLoginCall}. " +
              (TxViaConfigured ? $"Internet → radio transmitting is ON (channel {IgTxChannel}{(IgTxVia.Length > 0 ? " via " + IgTxVia : "")})." : "Internet → radio transmitting is off (no IGTXVIA).");
    }

    // ------------------------------------------------------------------ report

    public ObservableCollection<ServiceCard> Services { get; } = [];
    public ObservableCollection<DiagnosticRow> Warnings { get; } = [];

    private string _reportSummary = "";
    public string ReportSummary { get => _reportSummary; private set => Set(ref _reportSummary, value); }

    private List<string> _radioChannels = ["0"];
    public List<string> RadioChannels { get => _radioChannels; private set => Set(ref _radioChannels, value); }

    public RelayCommand DisableRowCommand { get; }
    public RelayCommand EnableRowCommand { get; }

    private void DisableRow(ServiceLineRow? row)
    {
        if (row == null || row.IsDisabled) return;
        if (!Dialogs.Confirm($"Disable line {row.LineNumber}?\n\n{row.Text}\n\nIt is commented out (not deleted) and can be enabled again.")) return;
        Session.Edit(d => d.DisableDirective(row.Index, "disabled on the Gateway & digipeater page"), $"{row.Keyword} on line {row.LineNumber} disabled.");
    }

    private void EnableRow(ServiceLineRow? row)
    {
        if (row == null || !row.IsDisabled) return;
        var info = DirectiveCatalog.Find(row.Keyword);
        if ((info?.CanTransmit == true || info?.ForwardsTraffic == true) &&
            !OperatorNotice.ConfirmTransmit($"Enable line {row.LineNumber}: {row.Text}", $"{row.Keyword}: {info?.Summary}"))
            return;
        Session.Edit(d =>
        {
            if (!d.EnableDirective(row.Index)) throw new InvalidOperationException("This line was not disabled by Dire Wolf Station.");
        }, $"Line {row.LineNumber} enabled.");
    }

    private void DisableKeyword(string keyword, string what)
    {
        if (!Dialogs.Confirm($"Turn off {what}? The {keyword} line is commented out (not deleted).")) return;
        Session.Edit(d => d.DisableDirective(keyword, null, "disabled on the Gateway & digipeater page"), $"{keyword} disabled.");
    }

    // ------------------------------------------------------------------ digipeater

    public ObservableCollection<ServiceLineRow> DigiRows { get; } = [];
    public IReadOnlyList<string> DigiOptions { get; } = ["", "OFF", "DROP", "MARK", "TRACE"];

    public const string StandardPresetText =
        "Standard WIDEn-N digipeater (Dire Wolf's sample rule): repeats packets addressed through your callsign, the alias TEST, WIDE1-1, and " +
        "WIDE2-1 / WIDE2-2 (decrementing the hop count). Paths asking for WIDE3-n or more are treated as an alias, so they are repeated once and " +
        "marked used instead of spreading further. TRACE inserts your callsign into the path.";

    public const string FillInPresetText =
        "Fill-in digipeater: only repeats packets whose next hop is WIDE1-1 (and packets addressed through your callsign), for a home station " +
        "that helps nearby low-power stations reach a wide-area digipeater. WIDE2-n is left to the wide-area digipeaters.";

    private ServiceLineRow? _editingDigi;
    public ServiceLineRow? EditingDigi { get => _editingDigi; private set { Set(ref _editingDigi, value); OnPropertyChanged(nameof(DigiFormTitle)); } }
    public string DigiFormTitle => EditingDigi is { } r ? $"Edit the DIGIPEAT rule on line {r.LineNumber}" : "New DIGIPEAT rule";

    private string _digiFrom = "0", _digiTo = "0", _digiAlias = "", _digiWide = "", _digiOption = "", _presetNote = "";
    public string DigiFrom { get => _digiFrom; set { if (Set(ref _digiFrom, value ?? "")) OnPropertyChanged(nameof(DigiPreview)); } }
    public string DigiTo { get => _digiTo; set { if (Set(ref _digiTo, value ?? "")) OnPropertyChanged(nameof(DigiPreview)); } }
    public string DigiAlias { get => _digiAlias; set { if (Set(ref _digiAlias, value ?? "")) OnPropertyChanged(nameof(DigiPreview)); } }
    public string DigiWide { get => _digiWide; set { if (Set(ref _digiWide, value ?? "")) OnPropertyChanged(nameof(DigiPreview)); } }
    public string DigiOption { get => _digiOption; set { if (Set(ref _digiOption, value ?? "")) OnPropertyChanged(nameof(DigiPreview)); } }
    public string PresetNote { get => _presetNote; private set => Set(ref _presetNote, value); }

    public string DigiPreview =>
        $"DIGIPEAT {DigiFrom} {DigiTo} {DigiAlias} {DigiWide}{(DigiOption.Length > 0 ? " " + DigiOption : "")}\n" +
        $"Packets heard on channel {DigiFrom} are transmitted on channel {DigiTo} when the next unused path element is your callsign, " +
        $"matches the alias pattern {Show(DigiAlias)} (replaced by your callsign and marked used), or matches the WIDEn-N pattern {Show(DigiWide)} " +
        "(hop count decremented). Duplicates within the DEDUPE time (default 30 s) are not repeated." +
        (DigiOption is "DROP" or "MARK" or "TRACE" ? $" Preemptive mode {DigiOption}: your callsign or alias is also acted on when it appears later in the path." : "");

    private static string Show(string p) => p.Length == 0 ? "(none)" : p;

    public RelayCommand PresetStandardCommand { get; }
    public RelayCommand PresetFillInCommand { get; }
    public RelayCommand NewDigiCommand { get; }
    public RelayCommand EditDigiCommand { get; }
    public RelayCommand SaveDigiCommand { get; }

    private void LoadDigi(ServiceLineRow? row)
    {
        if (row == null) return;
        EditingDigi = row;
        DigiFrom = row.Arg(0);
        DigiTo = row.Arg(1);
        DigiAlias = row.Arg(2);
        DigiWide = row.Arg(3);
        DigiOption = row.Arg(4).ToUpperInvariant();
        PresetNote = "";
    }

    private static string? CheckRegex(string pattern, string what)
    {
        if (pattern.Length == 0) return $"The {what} pattern is required.";
        if (pattern.Any(char.IsWhiteSpace)) return $"The {what} pattern cannot contain spaces (Dire Wolf splits the line at spaces).";
        if (pattern.Contains('#')) return $"The {what} pattern cannot contain '#'.";
        try { _ = new Regex(pattern); }
        catch (ArgumentException e) { return $"The {what} pattern is not a valid regular expression: {e.Message}"; }
        return null;
    }

    private void SaveDigi()
    {
        var facts = Session.Document == null ? null : ConfigFacts.From(Session.Document);
        if (facts == null) return;
        bool fromOk = ConfigFacts.TryInt(DigiFrom.Trim(), out int from) && facts.IsRadioChannel(from);
        bool toOk = ConfigFacts.TryInt(DigiTo.Trim(), out int to) && facts.IsRadioChannel(to);
        string? err =
            !fromOk ? $"Channel {DigiFrom} is not a configured radio channel." :
            !toOk ? $"Channel {DigiTo} is not a configured radio channel." :
            CheckRegex(DigiAlias.Trim(), "alias") ?? CheckRegex(DigiWide.Trim(), "WIDEn-N");
        if (err != null)
        {
            Dialogs.Warning(err, "Patterns are checked with .NET regular expressions; Dire Wolf uses POSIX extended regular expressions, which are very similar for these patterns.");
            return;
        }
        string body = $"DIGIPEAT {from} {to} {DigiAlias.Trim()} {DigiWide.Trim()}" + (DigiOption.Length > 0 ? " " + DigiOption : "");
        if (!OperatorNotice.ConfirmTransmit($"{(EditingDigi == null ? "Add" : "Change")} a digipeater rule:\n{body}",
                $"Your station will TRANSMIT on channel {to}, repeating other stations' packets that match, every time they are heard. " +
                "Only run a digipeater where one is needed and coordinate with local digipeater operators; a misconfigured digipeater can flood the channel."))
            return;
        var editing = EditingDigi;
        bool ok = Session.Edit(d =>
        {
            if (editing != null) d.ReplaceLine(editing.Index, ConfigLineQuery.Replacement(editing, body));
            else
            {
                var last = d.FindDirectives("DIGIPEAT").LastOrDefault();
                if (last == null) d.SetDirective("DIGIPEAT", [$"{from}", $"{to}", DigiAlias.Trim(), DigiWide.Trim(), .. DigiOption.Length > 0 ? new[] { DigiOption } : []]);
                else d.InsertLine(last.Index + 1, body);
            }
        }, "Digipeater rule changed.");
        if (ok) EditingDigi = null;
    }

    // ------------------------------------------------------------------ IGate

    private bool _igateConfigured, _txViaConfigured;
    public bool IgateConfigured { get => _igateConfigured; private set => Set(ref _igateConfigured, value); }
    public bool TxViaConfigured { get => _txViaConfigured; private set => Set(ref _txViaConfigured, value); }

    private string _igServer = "", _igLoginCall = "", _passcodeState = "", _igTxChannel = "0", _igTxVia = "", _igFilter = "", _igTxLimit1 = "", _igTxLimit5 = "", _igateState = "";
    public string IgServer { get => _igServer; set => Set(ref _igServer, value ?? ""); }
    public string IgLoginCall { get => _igLoginCall; set => Set(ref _igLoginCall, (value ?? "").ToUpperInvariant()); }
    public string PasscodeState { get => _passcodeState; private set => Set(ref _passcodeState, value); }
    public string IgTxChannel { get => _igTxChannel; set => Set(ref _igTxChannel, value ?? ""); }
    public string IgTxVia { get => _igTxVia; set => Set(ref _igTxVia, value ?? ""); }
    public string IgFilter { get => _igFilter; set => Set(ref _igFilter, value ?? ""); }
    public string IgTxLimit1 { get => _igTxLimit1; set => Set(ref _igTxLimit1, value ?? ""); }
    public string IgTxLimit5 { get => _igTxLimit5; set => Set(ref _igTxLimit5, value ?? ""); }
    public string IgateState { get => _igateState; private set => Set(ref _igateState, value); }

    public RelayCommand DisableIGateCommand { get; }
    public RelayCommand ApplyTxViaCommand { get; }
    public RelayCommand DisableTxViaCommand { get; }
    public RelayCommand ApplyIgFilterCommand { get; }
    public RelayCommand ApplyIgTxLimitCommand { get; }

    /// <summary>Called by the view with the PasswordBox content; the passcode goes only into the configuration document.</summary>
    public void ApplyIGate(string passcode)
    {
        var doc = Session.Document;
        if (doc == null) return;
        string server = IgServer.Trim(), call = IgLoginCall.Trim();
        passcode = passcode.Trim();
        if (server.Length == 0 || server.Any(char.IsWhiteSpace) && !Regex.IsMatch(server, @"^\S+\s+\d+$")) { Dialogs.Warning("Enter the APRS-IS server, e.g. noam.aprs2.net (optionally followed by a port)."); return; }
        if (ConfigEdit.Callsign(call) is { } ce) { Dialogs.Warning("IGate login: " + ce); return; }
        if (passcode.Length == 0)
        {
            var existing = doc.FindDirective("IGLOGIN");
            if (existing == null || existing.Arguments.Count < 2) { Dialogs.Warning("Enter your APRS-IS passcode."); return; }
            passcode = existing.Arguments[1];
        }
        else if (!Regex.IsMatch(passcode, @"^-?\d{1,5}$")) { Dialogs.Warning("An APRS-IS passcode is a number (-1 logs in without permission to send)."); return; }
        if (!OperatorNotice.ConfirmTransmit($"Configure the IGate: server {server}, login {call}.",
                "Your station will FORWARD APRS packets it hears on the radio to the Internet (APRS-IS) under your callsign. " +
                "Nothing is transmitted on the radio by this direction; Internet-to-radio needs IGTXVIA and is set separately. " +
                "The passcode is written only into the configuration file, never into the program's settings or logs."))
            return;
        var serverArgs = ConfigTokenizer.Split(server);
        Session.Edit(d =>
        {
            d.SetDirective("IGSERVER", serverArgs);
            d.SetDirective("IGLOGIN", [call, passcode]);
        }, "IGate configured.");
    }

    private void DisableIGate()
    {
        if (!Dialogs.Confirm("Turn the IGate off? IGSERVER, IGLOGIN and IGTXVIA lines are commented out (not deleted), so it can be enabled again.")) return;
        Session.Edit(d =>
        {
            d.DisableDirective("IGSERVER", null, "IGate turned off on the Gateway & digipeater page");
            d.DisableDirective("CWOPSERVER", null, "IGate turned off on the Gateway & digipeater page");
            d.DisableDirective("IGLOGIN", null, "IGate turned off on the Gateway & digipeater page");
            d.DisableDirective("IGTXVIA", null, "IGate turned off on the Gateway & digipeater page");
        }, "IGate turned off.");
    }

    private void ApplyTxVia()
    {
        var doc = Session.Document;
        if (doc == null) return;
        var facts = ConfigFacts.From(doc);
        if (!ConfigFacts.TryInt(IgTxChannel.Trim(), out int ch) || !facts.IsRadioChannel(ch)) { Dialogs.Warning($"Channel {IgTxChannel} is not a configured radio channel."); return; }
        string via = IgTxVia.Trim();
        if (via.Any(char.IsWhiteSpace) || via.Contains('#')) { Dialogs.Warning("The via path is one word, e.g. WIDE1-1 (several hops separated by commas, no spaces)."); return; }
        if (!facts.HasIGate) { Dialogs.Warning("Configure the IGate (server and login) first: Internet-to-radio needs it."); return; }
        if (!OperatorNotice.ConfirmTransmit($"Turn on Internet → radio transmitting on channel {ch}{(via.Length > 0 ? " via " + via : " (no path)")}?",
                "WARNING: your station will TRANSMIT traffic that comes from the Internet (APRS-IS), such as messages for stations heard nearby, " +
                "without you seeing each packet first. This is third-party traffic: check that it is permitted for your licence and in your country. " +
                "Keep the path short (WIDE1-1 or none) and set IGTXLIMIT so the radio channel is not flooded."))
            return;
        Session.Edit(d => d.SetDirective("IGTXVIA", via.Length > 0 ? [$"{ch}", via] : [$"{ch}"]), "Internet-to-radio (IGTXVIA) set.");
    }

    private void ApplyIgFilter()
    {
        string f = IgFilter.Trim();
        if (f.Length == 0) { DisableKeyword("IGFILTER", "the IGate server-side filter"); return; }
        if (f.Contains('#')) { Dialogs.Warning("The filter cannot contain '#'."); return; }
        Session.Edit(d => d.SetDirective("IGFILTER", [f]),
            "IGFILTER set. It asks APRS-IS for extra traffic; with IGTXVIA, matching packets may be transmitted on the radio.");
    }

    private void ApplyIgTxLimit()
    {
        if (ConfigEdit.WholeNumber(IgTxLimit1, 0, 1000, "The one-minute limit") is { } e1) { Dialogs.Warning(e1); return; }
        if (ConfigEdit.WholeNumber(IgTxLimit5, 0, 1000, "The five-minute limit") is { } e2) { Dialogs.Warning(e2); return; }
        Session.Edit(d => d.SetDirective("IGTXLIMIT", [IgTxLimit1.Trim(), IgTxLimit5.Trim()]), "IGTXLIMIT set.");
    }

    // ------------------------------------------------------------------ beacons

    public ObservableCollection<BeaconRow> Beacons { get; } = [];
    public IReadOnlyList<string> BeaconKinds => BeaconOptions.Kinds;

    private BeaconRow? _editingBeacon;
    public BeaconRow? EditingBeacon { get => _editingBeacon; private set { Set(ref _editingBeacon, value); OnPropertyChanged(nameof(BeaconFormTitle)); } }
    public string BeaconFormTitle => EditingBeacon is { } b ? $"Edit the {b.Kind} on line {b.Row.LineNumber}" : "New beacon";

    private string _beaconKind = "PBEACON", _beaconEvery = "30", _beaconDelay = "1", _beaconSendTo = "0", _beaconVia = "", _beaconOther = "";
    public string BeaconKind { get => _beaconKind; set => Set(ref _beaconKind, value ?? "PBEACON"); }
    public string BeaconEvery { get => _beaconEvery; set => Set(ref _beaconEvery, value ?? ""); }
    public string BeaconDelay { get => _beaconDelay; set => Set(ref _beaconDelay, value ?? ""); }
    public string BeaconSendTo { get => _beaconSendTo; set => Set(ref _beaconSendTo, value ?? ""); }
    public string BeaconVia { get => _beaconVia; set => Set(ref _beaconVia, value ?? ""); }
    public string BeaconOther { get => _beaconOther; set => Set(ref _beaconOther, value ?? ""); }

    public RelayCommand NewBeaconCommand { get; }
    public RelayCommand EditBeaconCommand { get; }
    public RelayCommand SaveBeaconCommand { get; }
    public RelayCommand DisableBeaconCommand { get; }
    public RelayCommand EnableBeaconCommand { get; }

    private void NewBeacon()
    {
        EditingBeacon = null;
        BeaconKind = "PBEACON";
        BeaconEvery = "30";
        BeaconDelay = "1";
        BeaconSendTo = "0";
        BeaconVia = "";
        BeaconOther = "symbol=/-\nlat=\nlong=\ncomment=";
    }

    private void LoadBeacon(BeaconRow? b)
    {
        if (b == null) return;
        EditingBeacon = b;
        BeaconKind = b.Kind;
        var opts = b.Options;
        BeaconEvery = BeaconOptions.Get(opts, "every") ?? "";
        BeaconDelay = BeaconOptions.Get(opts, "delay") ?? "";
        BeaconSendTo = BeaconOptions.Get(opts, "sendto") ?? "";
        BeaconVia = BeaconOptions.Get(opts, "via") ?? "";
        BeaconOther = BeaconOptions.ToEditorText(opts.Where(o => !(o.Key.Equals("every", StringComparison.OrdinalIgnoreCase) || o.Key.Equals("delay", StringComparison.OrdinalIgnoreCase)
            || o.Key.Equals("sendto", StringComparison.OrdinalIgnoreCase) || o.Key.Equals("via", StringComparison.OrdinalIgnoreCase))));
    }

    private void SaveBeacon()
    {
        var doc = Session.Document;
        if (doc == null) return;
        var opts = new List<KeyValuePair<string, string>>();
        if (BeaconDelay.Trim().Length > 0) opts.Add(new("delay", BeaconDelay.Trim()));
        if (BeaconEvery.Trim().Length > 0) opts.Add(new("every", BeaconEvery.Trim()));
        if (BeaconSendTo.Trim().Length > 0) opts.Add(new("sendto", BeaconSendTo.Trim()));
        if (BeaconVia.Trim().Length > 0) opts.Add(new("via", BeaconVia.Trim()));
        // Lines like "lat=" with nothing after them are dropped.
        var other = BeaconOptions.FromEditorText(string.Join("\n", BeaconOther.Replace("\r\n", "\n").Split('\n').Where(l => !Regex.IsMatch(l.Trim(), @"^[A-Za-z_]+=$"))));
        opts.AddRange(other);
        int? every = BeaconOptions.IntervalSeconds(BeaconEvery);
        if (BeaconEvery.Trim().Length > 0 && every is null or 0) { Dialogs.Warning("'every' is minutes, or minutes:seconds, e.g. 30 or 10:30."); return; }
        if (BeaconDelay.Trim().Length > 0 && BeaconOptions.IntervalSeconds(BeaconDelay) == null) { Dialogs.Warning("'delay' is minutes, or minutes:seconds."); return; }
        if (opts.Any(o => o.Key.Contains('#') || o.Value.Contains('#') && !o.Key.Equals("comment", StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Warning("Beacon options cannot contain '#' (except inside comment=).");
            return;
        }
        var tokens = BeaconOptions.ToTokens(opts);
        string body = BeaconKind + " " + string.Join(" ", tokens);
        string sendTo = BeaconSendTo.Trim().ToUpperInvariant();
        bool toIs = sendTo.StartsWith('I');
        int seconds = every ?? 600;
        string consequences = toIs
            ? $"This beacon is sent to APRS-IS through the IGate every {StationServicesAnalyzer.Interval(seconds)} (not on the radio)."
            : $"Your station will TRANSMIT this beacon on the radio automatically every {StationServicesAnalyzer.Interval(seconds)}{(BeaconVia.Trim().Length > 0 ? $", via {BeaconVia.Trim()} (repeated by digipeaters)" : "")}, unattended, whenever Dire Wolf runs.";
        if (!toIs && seconds < StationServicesAnalyzer.MinRecommendedBeaconInterval.TotalSeconds && BeaconKind != "TBEACON")
            consequences += $"\n\nThe interval is short: on shared APRS channels every 10 minutes or more (30 minutes for a fixed station) is the usual courtesy.";
        if (!OperatorNotice.ConfirmTransmit($"{(EditingBeacon == null ? "Add" : "Change")} a beacon:\n{body}", consequences)) return;
        var editing = EditingBeacon;
        bool ok = Session.Edit(d =>
        {
            if (editing != null) d.ReplaceLine(editing.Row.Index, ConfigLineQuery.Replacement(editing.Row, body));
            else
            {
                var last = d.Directives.LastOrDefault(l => l.Directive is "PBEACON" or "OBEACON" or "TBEACON" or "CBEACON" or "IBEACON");
                if (last == null) d.SetDirective(BeaconKind, tokens);
                else d.InsertLine(last.Index + 1, body);
            }
        }, "Beacon changed.");
        if (ok) NewBeacon();
    }

    // ------------------------------------------------------------------ filters

    public ObservableCollection<ServiceLineRow> FilterRows { get; } = [];

    // ------------------------------------------------------------------ forwarding log

    public ObservableCollection<ForwardingEvent> Events { get; } = [];

    private bool _showHeardViaDigi;
    /// <summary>Also list "Digipeater ..." lines (packets heard through other digipeaters; not this station's decisions).</summary>
    public bool ShowHeardViaDigi { get => _showHeardViaDigi; set => Set(ref _showHeardViaDigi, value); }

    public RelayCommand ClearEventsCommand { get; }

    private void OnConsoleLine(object? sender, string line)
    {
        if (ForwardingLog.Classify(line, ShowHeardViaDigi) is not { } c) return;
        Events.Insert(0, new ForwardingEvent(DateTimeOffset.Now, c.Kind, c.Brush, line.Trim()));
        while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
    }
}

/// <summary>A beacon line with its options parsed.</summary>
public sealed class BeaconRow
{
    public BeaconRow(ServiceLineRow row, ConfigFacts facts)
    {
        Row = row;
        Options = BeaconOptions.Parse(row.Args);
        Kind = row.Keyword;
        int? every = BeaconOptions.IntervalSeconds(BeaconOptions.Get(Options, "every"));
        int? delay = BeaconOptions.IntervalSeconds(BeaconOptions.Get(Options, "delay"));
        string sendTo = BeaconOptions.Get(Options, "sendto") ?? "";
        bool toIs = sendTo.StartsWith('I') || sendTo.StartsWith('i');
        Where = sendTo.Length == 0 ? "radio channel 0 (default)" : toIs ? "APRS-IS (IGate)" : sendTo.StartsWith('R') || sendTo.StartsWith('r') ? $"simulated receive on {sendTo[1..]}" : $"radio channel {sendTo.TrimStart('T', 't', 'X', 'x')}";
        Timing = $"every {IntervalText.Format(every ?? (Kind == "TBEACON" ? null : 600))}, first after {IntervalText.Format(delay ?? 60)}";
        Via = BeaconOptions.Get(Options, "via") ?? "";
        OptionsText = string.Join("  ", Options.Select(o => o.Value.Length == 0 ? o.Key : o.Key + "=" + o.Value));
        if (!row.IsDisabled && !toIs && Kind != "TBEACON" && (every ?? 600) < StationServicesAnalyzer.MinRecommendedBeaconInterval.TotalSeconds)
            Warning = "Short interval: on shared APRS channels every 10 minutes or more (30 minutes for a fixed station) is the usual courtesy.";
    }

    public ServiceLineRow Row { get; }
    public List<KeyValuePair<string, string>> Options { get; }
    public string Kind { get; }
    public string Where { get; }
    public string Timing { get; }
    public string Via { get; }
    public string OptionsText { get; }
    public string? Warning { get; }
    public string Title => $"{Kind} (line {Row.LineNumber}, {Row.State})";
}
