using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using DireWolfGui.Core.Config;
using DireWolfGui.Core.Integrations;
using DireWolfGui.Infrastructure;
using DireWolfGui.Views;

namespace DireWolfGui.ViewModels;

/// <summary>
/// Configuration editor: guided forms, every directive, raw text, diagnostics (GUI and real parser),
/// backups and profiles.  All edits are targeted ConfigDocument edits held in the shared
/// <see cref="ConfigurationSession"/>; Save shows a diff, backs up and writes atomically.
/// </summary>
public sealed class ConfigurationViewModel : PageViewModel
{
    public const int GuidedTab = 0, AllTab = 1, RawTab = 2, DiagnosticsTab = 3, BackupsTab = 4, ProfilesTab = 5;

    private readonly IShell _shell;
    private int _builtVersion = -1;
    private IReadOnlyList<AudioDevice> _inputs = [];
    private IReadOnlyList<AudioDevice> _outputs = [];
    private List<DirectiveRow> _allRows = [];
    private List<(string Call, string Secret)> _rawSecrets = [];
    private DateTime? _rawValidateAt;

    public ConfigurationViewModel(IShell shell) : base("Configuration", "\uE70F")
    {
        _shell = shell;
        Session = ConfigurationSession.For(shell);
        Session.DocumentChanged += (_, _) => Rebuild();

        OpenCommand = new AsyncCommand(OpenAsync);
        NewCommand = new RelayCommand(() => _shell.Navigate<SetupWizardViewModel>());
        SaveCommand = new AsyncCommand(async () => { await Session.SaveAsync(); }, () => Session.HasDocument);
        RevertCommand = new AsyncCommand(RevertAsync, () => Session.IsDirty || Session.IsStale);
        ReloadCommand = new AsyncCommand(ReloadAsync, () => Session.Path != null);
        OpenFolderCommand = new RelayCommand(() => OpenFolder(Path.GetDirectoryName(Session.Path ?? "")), () => Session.Path != null);

        AddChannelCommand = new RelayCommand(AddChannel, () => SelectedNewChannel is int);
        AddDeviceCommand = new RelayCommand(AddDevice, () => NextDevice is int);
        RefreshAudioCommand = new AsyncCommand(async () => { await LoadAudioDevicesAsync(); _builtVersion = -1; Rebuild(); });
        AddKissPortCommand = new RelayCommand(AddKissPort);
        ApplyKissPortCommand = new RelayCommand(p => ApplyKissPort(p as KissPortRow), p => p is KissPortRow { IsChanged: true });
        DisableKissPortCommand = new RelayCommand(p => DisableLine(p is KissPortRow k ? k.Index : -1, "KISSPORT"), p => p is KissPortRow);

        DisableLineCommand = new RelayCommand(p => { if (p is DirectiveRow r) DisableLine(r.Index, r.Keyword); }, p => p is DirectiveRow { IsDisabled: false });
        EnableLineCommand = new RelayCommand(p => EnableLine(p as DirectiveRow), p => p is DirectiveRow { IsDisabled: true });
        ShowInTextCommand = new RelayCommand(p => { if (p is DirectiveRow r) GoToLine(r.LineNumber); else if (p is DiagnosticRow { Line: int l }) GoToLine(l); });

        ApplyRawCommand = new RelayCommand(ApplyRaw, () => IsRawModified && Session.HasDocument);
        DiscardRawCommand = new RelayCommand(() => { IsRawModified = false; RefreshRaw(); }, () => IsRawModified);
        ValidateRawCommand = new RelayCommand(ValidateRaw, () => Session.HasDocument);

        CheckCommand = new AsyncCommand(CheckWithDireWolfAsync, () => Session.HasDocument && !IsChecking);

        RefreshBackupsCommand = new AsyncCommand(RefreshBackupsAsync);
        ViewBackupCommand = new AsyncCommand(p => ViewBackupAsync(p as BackupRow), p => p is BackupRow);
        RestoreBackupCommand = new AsyncCommand(p => RestoreBackupAsync(p as BackupRow), p => p is BackupRow && Session.Path != null);
        OpenBackupFolderCommand = new RelayCommand(() => OpenFolder(_shell.Backups.BackupDirectory));

        RefreshProfilesCommand = new AsyncCommand(RefreshProfilesAsync);
        SaveAsProfileCommand = new AsyncCommand(SaveAsProfileAsync, () => Session.HasDocument);
        OpenProfileCommand = new AsyncCommand(p => OpenProfileAsync(p as ProfileRow), p => p is ProfileRow);
        DuplicateProfileCommand = new AsyncCommand(p => DuplicateProfileAsync(p as ProfileRow), p => p is ProfileRow);
        RenameProfileCommand = new AsyncCommand(p => RenameProfileAsync(p as ProfileRow), p => p is ProfileRow);
        ImportProfileCommand = new AsyncCommand(ImportProfileAsync);
        ExportProfileCommand = new AsyncCommand(p => ExportProfileAsync(p as ProfileRow), p => p is ProfileRow);
        DeleteProfileCommand = new AsyncCommand(p => DeleteProfileAsync(p as ProfileRow), p => p is ProfileRow);
        OpenProfilesFolderCommand = new RelayCommand(() => OpenFolder(_shell.Profiles.ProfilesDirectory));
    }

    public ConfigurationSession Session { get; }

    // ------------------------------------------------------------------ page state

    private int _selectedTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value)) return;
            if (value == BackupsTab) RefreshBackupsAsync().Forget(_shell, "Backups");
            if (value == ProfilesTab) RefreshProfilesAsync().Forget(_shell, "Profiles");
        }
    }

    public bool HasDocument => Session.HasDocument;
    public bool NoDocument => !Session.HasDocument;

    public string NoDocumentText =>
        Session.LoadError ?? (string.IsNullOrWhiteSpace(_shell.ConfigPath)
            ? "No configuration file is selected yet. Open an existing direwolf.conf, open a saved profile (Profiles tab), or create a new configuration with the Setup wizard."
            : "Loading the configuration…");

    public AsyncCommand OpenCommand { get; }
    public RelayCommand NewCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand RevertCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    protected override async void OnShown()
    {
        try
        {
            if (_inputs.Count == 0 && _outputs.Count == 0) await LoadAudioDevicesAsync();
            await Session.EnsureCurrentAsync();
            Rebuild();
            if (!Session.HasDocument) SelectedTab = ProfilesTab;
            if (SelectedTab == BackupsTab) await RefreshBackupsAsync();
            if (SelectedTab == ProfilesTab) await RefreshProfilesAsync();
        }
        catch (Exception e)
        {
            _shell.SetStatus("Configuration page: " + e.Message);
        }
    }

    public override void Tick()
    {
        if (_rawValidateAt is DateTime t && DateTime.UtcNow >= t)
        {
            _rawValidateAt = null;
            ValidateRaw();
        }
    }

    private async Task LoadAudioDevicesAsync()
    {
        var (i, o) = await Task.Run(() => (WinMmAudio.InputDevices(), WinMmAudio.OutputDevices()));
        _inputs = i;
        _outputs = o;
    }

    private async Task OpenAsync()
    {
        var path = Dialogs.OpenFile("Dire Wolf configuration (*.conf)|*.conf|All files (*.*)|*.*", Session.Path ?? _shell.ConfigPath, "Open a Dire Wolf configuration");
        if (path == null) return;
        if (!await Session.ConfirmLeaveAsync("open another file")) return;
        _shell.SelectConfig(path);
        await Session.EnsureCurrentAsync();
        if (Session.HasDocument && SelectedTab == ProfilesTab) SelectedTab = GuidedTab;
    }

    private async Task RevertAsync()
    {
        if (Session.IsDirty && !Dialogs.Confirm("Discard all unsaved changes and reload the file from disk?")) return;
        IsRawModified = false;
        await Session.RevertAsync();
    }

    private async Task ReloadAsync()
    {
        if (Session.IsDirty && !Dialogs.Confirm("Reload the file from disk? Unsaved changes are discarded.")) return;
        IsRawModified = false;
        if (Session.Path != null) await Session.LoadAsync(Session.Path);
    }

    private void OpenFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        var r = ExternalAppLauncher.OpenFolder(dir);
        if (!r.Success) Dialogs.Error("The folder could not be opened.", r.Error);
    }

    // ------------------------------------------------------------------ rebuild from the document

    private void Rebuild()
    {
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(NoDocument));
        OnPropertyChanged(nameof(NoDocumentText));
        var doc = Session.Document;
        if (doc == null)
        {
            _builtVersion = Session.Version;
            IdentityFields.Clear(); Devices.Clear(); Channels.Clear(); NetworkFields.Clear(); KissPorts.Clear();
            LoggingFields.Clear(); GpsFields.Clear(); DirectiveRows.Clear(); GuiDiagnostics.Clear();
            _allRows = [];
            RawText = "";
            IsRawModified = false;
            return;
        }
        if (_builtVersion == Session.Version) return;
        _builtVersion = Session.Version;

        var diagnostics = ConfigValidator.Validate(doc, _shell.LastCheck?.Summary);
        GuiDiagnostics.ReplaceWith(diagnostics.OrderBy(d => d.Line ?? 0).Select(d => new DiagnosticRow(d)));
        OnPropertyChanged(nameof(DiagnosticsSummary));
        BuildGuided(doc);
        BuildDirectiveRows(doc, diagnostics);
        if (IsRawModified) RawConflict = true;
        else RefreshRaw();
    }

    // ------------------------------------------------------------------ (a) guided

    public ObservableCollection<GuidedField> IdentityFields { get; } = [];
    public ObservableCollection<GuidedDevice> Devices { get; } = [];
    public ObservableCollection<GuidedChannel> Channels { get; } = [];
    public ObservableCollection<GuidedField> NetworkFields { get; } = [];
    public ObservableCollection<KissPortRow> KissPorts { get; } = [];
    public ObservableCollection<GuidedField> LoggingFields { get; } = [];
    public ObservableCollection<GuidedField> GpsFields { get; } = [];

    private List<int> _availableNewChannels = [];
    public List<int> AvailableNewChannels { get => _availableNewChannels; private set => Set(ref _availableNewChannels, value); }

    private int? _selectedNewChannel;
    public int? SelectedNewChannel { get => _selectedNewChannel; set => Set(ref _selectedNewChannel, value); }

    private int? _nextDevice;
    public int? NextDevice { get => _nextDevice; private set { Set(ref _nextDevice, value); OnPropertyChanged(nameof(AddDeviceText)); } }
    public string AddDeviceText => NextDevice is int n ? $"Add audio device {n} (ADEVICE{n})" : "All three audio devices are defined";

    private string _kissDefaultNote = "";
    public string KissDefaultNote { get => _kissDefaultNote; private set => Set(ref _kissDefaultNote, value); }

    private string _newKissPort = "";
    public string NewKissPort { get => _newKissPort; set => Set(ref _newKissPort, value); }
    private string _newKissChannel = "";
    public string NewKissChannel { get => _newKissChannel; set => Set(ref _newKissChannel, value); }

    public RelayCommand AddChannelCommand { get; }
    public RelayCommand AddDeviceCommand { get; }
    public AsyncCommand RefreshAudioCommand { get; }
    public RelayCommand AddKissPortCommand { get; }
    public RelayCommand ApplyKissPortCommand { get; }
    public RelayCommand DisableKissPortCommand { get; }

    public string TcpBindWarning =>
        "TCPBIND ANY (Dire Wolf's default when no TCPBIND line is present) lets programs on OTHER computers connect to the AGW and KISS ports, " +
        "and anything that connects can transmit with your callsign. Prefer TCPBIND LOCAL unless remote clients are really needed, and keep the ports closed in the firewall.";

    private void BuildGuided(ConfigDocument doc)
    {
        var pending = new Dictionary<string, string>();
        if (Session.IsDirty)
            foreach (var f in IdentityFields.Concat(Devices.SelectMany(d => d.Fields)).Concat(Channels.SelectMany(c => c.Fields))
                         .Concat(NetworkFields).Concat(LoggingFields).Concat(GpsFields))
                if (f.IsChanged) pending[f.Key] = f.Value;

        GuidedField F(string keyword, int? context, string label, string? explanation = null, string? def = null, IEnumerable<string>? choices = null,
            Func<string, string?>? validate = null, Func<string, bool>? confirm = null, Func<string, string>? normalize = null)
        {
            var f = new GuidedField(Session, doc, keyword, context, label, explanation, def, choices, validate, confirm, normalize);
            if (pending.TryGetValue(f.Key, out var v)) f.Value = v;
            return f;
        }

        var facts = ConfigFacts.From(doc);
        var channelLines = doc.FindDirectives("CHANNEL").Select(l => ConfigFacts.TryInt(l.Arguments.FirstOrDefault(), out int n) ? n : -1).Where(n => n is >= 0 and < 6);
        var channels = facts.Channels.Keys.Where(facts.IsRadioChannel).Concat(channelLines).Append(0).Distinct().OrderBy(n => n).ToList();

        // Identity
        IdentityFields.ReplaceWith(channels.Select(ch => F("MYCALL", ch, $"Callsign, channel {ch}",
            ch == 0 ? "Your callsign with an optional SSID (e.g. -10 for an IGate/digipeater). A MYCALL also applies to later channels that have none of their own."
                    : "Callsign for this channel; leave unset to use the one from channel 0.",
            null, null, ConfigEdit.Callsign, null, v => v.ToUpperInvariant())));

        // Audio devices
        var devs = facts.DefinedDevices.Append(0).Distinct().OrderBy(n => n).ToList();
        Devices.ReplaceWith(devs.Select(n => new GuidedDevice(new AudioDeviceEditor(Session, doc, n, _inputs, _outputs),
        [
            F("ARATE", n, "Sample rate", "Samples per second. 44100 or 48000 work with all modems; higher rates only use more CPU.", "44100",
                ["44100", "48000", "22050", "11025", "96000"], v => ConfigEdit.WholeNumber(v, 8000, 192000, "The sample rate")),
            F("ACHANNELS", n, "Audio channels", "1 = mono, one radio. 2 = stereo: two radios on the left and right channels of the same sound card (channels " + (n * 2) + " and " + (n * 2 + 1) + ").", "1",
                ["1", "2"], v => v is "1" or "2" ? null : "Use 1 (mono) or 2 (stereo)."),
        ])));
        NextDevice = Enumerable.Range(1, 2).Cast<int?>().FirstOrDefault(n => !facts.DefinedDevices.Contains(n!.Value) && facts.DefinedDevices.Contains(n.Value - 1));

        // Channels
        Channels.ReplaceWith(channels.Select(ch =>
        {
            var cf = facts.Channel(ch);
            bool valid = facts.IsRadioChannel(ch);
            string modem = cf.IsWapr ? $"experimental WAPR {cf.WaprProfile}" : cf.Modem != null ? $"{cf.Modem} bps" : "1200 bps (default)";
            string? note = !valid
                ? $"Channel {ch} is not a valid radio channel: audio device {ch / 2} is not defined{(ch % 2 == 1 ? " or not in stereo (ACHANNELS 2)" : "")}. Dire Wolf ignores its settings."
                : cf.IsWapr ? "This channel uses the experimental WAPR modem. Change its WAPR settings on the WAPR (experimental) page." : null;
            bool wasWapr = cf.IsWapr;
            var fields = new List<GuidedField>
            {
                F("MODEM", ch, "Modem", "Speed (300, 1200, 2400, 4800, 9600) optionally followed by options such as demodulator profiles, as in the User Guide. 1200 is the usual VHF/UHF APRS speed, 300 is used on HF.", "1200",
                    ["1200", "300", "2400", "4800", "9600"],
                    v => v.StartsWith("WAPR", StringComparison.OrdinalIgnoreCase) ? "WAPR is experimental: set it up on the WAPR (experimental) page, which explains what it changes."
                        : !(char.IsAsciiDigit(v[0]) || v.StartsWith("AIS", StringComparison.OrdinalIgnoreCase) || v.StartsWith("EAS", StringComparison.OrdinalIgnoreCase)) ? "The modem setting starts with a speed, e.g. 1200." : null,
                    v => !wasWapr || Dialogs.Confirm($"Channel {ch} is currently an experimental WAPR channel. Change it back to an AX.25 modem ({v})?\n\nWAPR gateway rules (WAPRGATE) for this channel stop working.", "Leave WAPR", warning: true)),
                F("TXDELAY", ch, "TXDELAY", "Time from keying the transmitter to sending data, in 10 ms units. The radio needs time to reach full power; VOX needs more.", "30 (300 ms)", null, v => ConfigEdit.WholeNumber(v, 0, 255)),
                F("TXTAIL", ch, "TXTAIL", "Time from the end of data to unkeying, in 10 ms units, so the end of the frame is not cut off.", "10 (100 ms)", null, v => ConfigEdit.WholeNumber(v, 0, 255)),
                F("DWAIT", ch, "DWAIT", "Extra time to wait after the channel becomes clear, in 10 ms units, for receivers with slow squelch. Usually 0.", "0", null, v => ConfigEdit.WholeNumber(v, 0, 255)),
                F("SLOTTIME", ch, "SLOTTIME", "Channel access: time slot between attempts, in 10 ms units.", "10 (100 ms)", null, v => ConfigEdit.WholeNumber(v, 0, 255)),
                F("PERSIST", ch, "PERSIST", "Channel access: probability (out of 256) of transmitting in each slot once the channel is clear.", "63", null, v => ConfigEdit.WholeNumber(v, 0, 255)),
                F("FULLDUP", ch, "Full duplex", "ON transmits without waiting for a clear channel — only for full-duplex links such as satellites.", "OFF", ["OFF", "ON"],
                    v => v.Equals("ON", StringComparison.OrdinalIgnoreCase) || v.Equals("OFF", StringComparison.OrdinalIgnoreCase) ? null : "Use ON or OFF.", null, v => v.ToUpperInvariant()),
                F("FX25TX", ch, "FX.25 transmit", "Adds forward error correction (16, 32 or 64 check bytes). Stations without FX.25 still decode the normal AX.25 part.", "0 (off)", ["0", "16", "32", "64"],
                    v => ConfigEdit.WholeNumber(v, 0, 64)),
                F("IL2PTX", ch, "IL2P transmit", "Transmit IL2P instead of AX.25. Only stations that support IL2P can decode it.", "not used (AX.25)", ["0", "1", "+0", "+1"]),
            };
            return new GuidedChannel(ch, $"Channel {ch} (audio device {ch / 2}, {(ch % 2 == 0 ? "left / mono" : "right")}) — {modem}", note, cf.IsWapr, fields,
                new PttEditor(Session, doc, ch));
        }));
        AvailableNewChannels = Enumerable.Range(0, 6).Where(n => facts.IsRadioChannel(n) && !channels.Contains(n)).ToList();
        if (SelectedNewChannel is not int sel || !AvailableNewChannels.Contains(sel)) SelectedNewChannel = AvailableNewChannels.Cast<int?>().FirstOrDefault();

        // Network
        NetworkFields.ReplaceWith(
        [
            F("AGWPORT", null, "AGW port", "TCP port for AGWPE-compatible programs (APRS clients, packet terminals). 0 turns the AGW server off. Any program that connects can transmit.", "8000",
                ["8000", "0"], v => ConfigEdit.WholeNumber(v, 0, 65535, "The port")),
            F("TCPBIND", null, "Network access", "LOCAL: only programs on this computer can connect to the AGW/KISS ports. ANY: other computers too.", "ANY (all network interfaces)",
                ["LOCAL", "ANY"], v => v.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) || v.Equals("ANY", StringComparison.OrdinalIgnoreCase) ? null : "Use LOCAL or ANY.",
                v => !v.Equals("ANY", StringComparison.OrdinalIgnoreCase) || OperatorNotice.ConfirmTransmit("Allow other computers to connect to the AGW and KISS ports (TCPBIND ANY)?", TcpBindWarning),
                v => v.ToUpperInvariant()),
        ]);
        KissPorts.ReplaceWith(doc.FindDirectives("KISSPORT").Select(l => new KissPortRow(l)));
        KissDefaultNote = facts.KissPorts.Any(k => k.Line == null)
            ? "Dire Wolf also opens the default KISS TCP port 8001 (no KISSPORT 0 line)."
            : "The default KISS port 8001 is not opened (KISSPORT 0 or another KISSPORT line replaces it).";

        // Logging, GPS
        LoggingFields.ReplaceWith(
        [
            F("LOGDIR", null, "Log folder", "Folder for daily CSV files of received packets (YYYY-MM-DD.log). Dire Wolf Station can also turn this on with the -l option (Settings).", null, null,
                null, null, v => v.Contains(' ') && !v.StartsWith('"') ? ConfigTokenizer.Quote(v) : v),
            F("LOGFILE", null, "Single log file", "One CSV file for received packets instead of daily files.", null, null,
                null, null, v => v.Contains(' ') && !v.StartsWith('"') ? ConfigTokenizer.Quote(v) : v),
        ]);
        GpsFields.ReplaceWith(
        [
            F("GPSNMEA", null, "GPS on a serial port", "Serial port (and optional speed) of an NMEA GPS receiver, e.g. COM7 4800. Used by tracker beacons and for the station position.", null, ConfigEdit.ComPorts),
            F("GPSD", null, "gpsd server", "Use a gpsd server (host and port). Not available in all Windows builds.", null, ["localhost 2947"]),
        ]);
    }

    private void AddChannel()
    {
        if (SelectedNewChannel is not int ch) return;
        Session.Edit(d => d.SetDirective("MODEM", ["1200"], ch), $"Channel {ch} added with MODEM 1200 (no PTT: receive only).");
    }

    private void AddDevice()
    {
        if (NextDevice is not int n) return;
        string input = _inputs.Count > 0 ? WinMmAudio.AdeviceToken(_inputs[0], _inputs) : "0";
        string output = _outputs.Count > 0 ? WinMmAudio.AdeviceToken(_outputs[0], _outputs) : "0";
        Session.Edit(d =>
        {
            d.SetDirective($"ADEVICE{n}", [input, output], n);
            d.SetDirective("ACHANNELS", ["1"], n);
        }, $"Audio device {n} added; choose its sound devices.");
    }

    private void AddKissPort()
    {
        string port = NewKissPort.Trim(), ch = NewKissChannel.Trim();
        if (ConfigEdit.WholeNumber(port, 1, 65535, "The KISS port") is { } e1) { Dialogs.Warning(e1); return; }
        if (ch.Length > 0 && ConfigEdit.WholeNumber(ch, 0, 15, "The channel") is { } e2) { Dialogs.Warning(e2); return; }
        if (KissPorts.Any(k => k.CurrentPort == port)) { Dialogs.Warning($"KISSPORT {port} is already listed."); return; }
        var args = ch.Length > 0 ? new[] { port, ch } : [port];
        bool ok = Session.Edit(d =>
        {
            var last = d.FindDirectives("KISSPORT").LastOrDefault();
            if (last == null) d.SetDirective("KISSPORT", args);
            else d.InsertLine(last.Index + 1, "KISSPORT " + string.Join(" ", args));
        }, $"KISSPORT {port} added.");
        if (ok) { NewKissPort = ""; NewKissChannel = ""; }
    }

    private void ApplyKissPort(KissPortRow? row)
    {
        if (row == null) return;
        string port = row.Port.Trim(), ch = row.Channel.Trim();
        if (ConfigEdit.WholeNumber(port, 0, 65535, "The KISS port") is { } e1) { Dialogs.Warning(e1); return; }
        if (ch.Length > 0 && ConfigEdit.WholeNumber(ch, 0, 15, "The channel") is { } e2) { Dialogs.Warning(e2); return; }
        string text = row.Indent + "KISSPORT " + port + (ch.Length > 0 ? " " + ch : "") + (row.TrailingComment ?? "");
        Session.Edit(d => d.ReplaceLine(row.Index, text), $"KISSPORT line {row.LineNumber} changed.");
    }

    // ------------------------------------------------------------------ (b) all directives

    public ObservableCollection<DirectiveRow> DirectiveRows { get; } = [];

    public IReadOnlyList<string> CategoryFilters { get; } =
        new[] { "All", "With problems", "Can transmit or forward", "Disabled by Dire Wolf Station", "Unknown" }
            .Concat(Enum.GetNames<DirectiveCategory>()).ToList();

    private string _filterText = "";
    public string FilterText { get => _filterText; set { if (Set(ref _filterText, value ?? "")) ApplyFilter(); } }

    private string _categoryFilter = "All";
    public string CategoryFilter { get => _categoryFilter; set { if (Set(ref _categoryFilter, value ?? "All")) ApplyFilter(); } }

    private string _directiveCountText = "";
    public string DirectiveCountText { get => _directiveCountText; private set => Set(ref _directiveCountText, value); }

    public RelayCommand DisableLineCommand { get; }
    public RelayCommand EnableLineCommand { get; }
    public RelayCommand ShowInTextCommand { get; }

    private void BuildDirectiveRows(ConfigDocument doc, IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        var byLine = diagnostics.Where(d => d.Line != null).GroupBy(d => d.Line!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<DirectiveRow>();
        foreach (var l in doc.Lines)
        {
            bool disabled = l.Kind == ConfigLineKind.Comment && l.Text.TrimStart().StartsWith(ConfigDocument.DisabledMarker, StringComparison.Ordinal);
            if (!l.IsDirective && !disabled) continue;
            string body = disabled ? l.Text.TrimStart()[ConfigDocument.DisabledMarker.Length..] : l.Text;
            var tokens = ConfigTokenizer.Split(body);
            if (tokens.Count == 0) continue;
            var info = DirectiveCatalog.Find(tokens[0]);
            string keyword = info?.Name ?? tokens[0].ToUpperInvariant();
            byLine.TryGetValue(l.LineNumber, out var ds);
            var worst = ds?.Max(d => d.Severity);
            rows.Add(new DirectiveRow
            {
                Index = l.Index,
                LineNumber = l.LineNumber,
                Keyword = keyword,
                Text = PasscodeMask.MaskLine(l.Text.Trim()),
                ContextText = info?.Scope switch
                {
                    DirectiveScope.Channel => $"channel {l.Channel}",
                    DirectiveScope.AudioDevice => $"device {(info.Name == "ADEVICE" && !disabled ? l.AudioDeviceDefined ?? 0 : l.AudioDevice)}",
                    _ => "",
                },
                Summary = info?.Summary ?? "Not a Dire Wolf directive: Dire Wolf reports it as unrecognized and ignores the line.",
                Category = info?.Category.ToString() ?? "Unknown",
                Diagnostics = ds == null ? "" : string.Join("\n", ds.Select(d => $"{d.Severity}: {d.Message}")),
                SeverityKind = worst is DiagnosticSeverity s ? ConfigEdit.SeverityKind(s) : "",
                IsDisabled = disabled,
                IsUnknown = info == null,
                Transmits = info?.CanTransmit == true,
                Forwards = info?.ForwardsTraffic == true,
            });
        }
        _allRows = rows;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<DirectiveRow> q = _allRows;
        q = CategoryFilter switch
        {
            "All" => q,
            "With problems" => q.Where(r => r.HasDiagnostics),
            "Can transmit or forward" => q.Where(r => r.Transmits || r.Forwards),
            "Disabled by Dire Wolf Station" => q.Where(r => r.IsDisabled),
            "Unknown" => q.Where(r => r.IsUnknown),
            var c => q.Where(r => r.Category == c),
        };
        if (FilterText.Trim().Length > 0)
            q = q.Where(r => r.SearchText.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        DirectiveRows.ReplaceWith(list);
        DirectiveCountText = $"{list.Count} of {_allRows.Count} lines";
    }

    private void DisableLine(int index, string keyword)
    {
        if (index < 0 || Session.Document == null) return;
        var line = Session.Document.Lines.ElementAtOrDefault(index);
        if (line == null || !line.IsDirective) return;
        if (!Dialogs.Confirm($"Disable line {line.LineNumber}?\n\n{PasscodeMask.MaskLine(line.Text.Trim())}\n\nThe line is commented out with a \"disabled by Dire Wolf Station\" marker, not deleted, and can be enabled again."))
            return;
        Session.Edit(d => d.DisableDirective(index, "disabled in the Configuration page"), $"{keyword} on line {line.LineNumber} disabled.");
    }

    private void EnableLine(DirectiveRow? row)
    {
        if (row == null || !row.IsDisabled) return;
        if ((row.Transmits || row.Forwards) &&
            !OperatorNotice.ConfirmTransmit($"Enable line {row.LineNumber}: {row.Text.Replace(ConfigDocument.DisabledMarker, "")}",
                $"{row.Keyword}: {row.Summary}\nThis setting can make the station transmit or forward traffic once Dire Wolf is (re)started."))
            return;
        Session.Edit(d =>
        {
            if (!d.EnableDirective(row.Index)) throw new InvalidOperationException("This line was not disabled by Dire Wolf Station; edit it on the Raw text tab.");
        }, $"Line {row.LineNumber} enabled.");
    }

    // ------------------------------------------------------------------ (c) raw text

    private string _rawText = "";
    /// <summary>The document text with APRS-IS passcodes shown as *****.</summary>
    public string RawText
    {
        get => _rawText;
        set
        {
            if (!Set(ref _rawText, value ?? "")) return;
            if (!_settingRaw)
            {
                IsRawModified = true;
                _rawValidateAt = DateTime.UtcNow.AddSeconds(1);
            }
        }
    }

    private bool _settingRaw;

    private bool _isRawModified;
    public bool IsRawModified
    {
        get => _isRawModified;
        set { if (Set(ref _isRawModified, value) && !value) RawConflict = false; }
    }

    private bool _rawConflict;
    public bool RawConflict { get => _rawConflict; private set => Set(ref _rawConflict, value); }

    public ObservableCollection<DiagnosticRow> RawDiagnostics { get; } = [];

    private DiagnosticRow? _selectedRawDiagnostic;
    public DiagnosticRow? SelectedRawDiagnostic
    {
        get => _selectedRawDiagnostic;
        set
        {
            if (Set(ref _selectedRawDiagnostic, value) && value?.Line is int l) GoToLineRequested?.Invoke(this, l);
        }
    }

    /// <summary>Raised to move the raw editor's caret to a 1-based line.</summary>
    public event EventHandler<int>? GoToLineRequested;

    public RelayCommand ApplyRawCommand { get; }
    public RelayCommand DiscardRawCommand { get; }
    public RelayCommand ValidateRawCommand { get; }

    private void GoToLine(int line)
    {
        SelectedTab = RawTab;
        GoToLineRequested?.Invoke(this, line);
    }

    private void RefreshRaw()
    {
        var doc = Session.Document;
        _rawSecrets = [];
        _settingRaw = true;
        RawText = doc == null ? "" : PasscodeMask.MaskText(doc.ToText(), _rawSecrets);
        _settingRaw = false;
        RawConflict = false;
        RawDiagnostics.ReplaceWith(GuiDiagnostics);
    }

    private string? RawToText(out string? error)
    {
        error = null;
        var doc = Session.Document;
        if (doc == null) return null;
        string text = PasscodeMask.UnmaskText(RawText, _rawSecrets);
        if (!doc.ToText().Contains('\r')) text = text.Replace("\r\n", "\n");
        if (doc.Encoding.CodePage == Encoding.Latin1.CodePage && text.Any(c => c > 'ÿ'))
            error = "This file is not UTF-8 (it is kept as Latin-1 so no byte changes), and the text contains characters Latin-1 cannot store.";
        return text;
    }

    private ConfigDocument? BuildRawDocument(string text)
    {
        var doc = Session.Document!;
        var body = doc.Encoding.GetBytes(text);
        byte[] bytes = doc.HasBom ? [0xEF, 0xBB, 0xBF, .. body] : body;
        return ConfigDocument.FromBytes(bytes);
    }

    private void ValidateRaw()
    {
        if (Session.Document == null) return;
        var text = RawToText(out _);
        if (text == null) return;
        var doc = BuildRawDocument(text);
        if (doc == null) return;
        RawDiagnostics.ReplaceWith(ConfigValidator.Validate(doc, _shell.LastCheck?.Summary).OrderBy(d => d.Line ?? 0).Select(d => new DiagnosticRow(d)));
    }

    private void ApplyRaw()
    {
        var text = RawToText(out var error);
        if (text == null) return;
        if (error != null) { Dialogs.Error("The text cannot be applied.", error); return; }
        var doc = BuildRawDocument(text);
        if (doc == null) return;
        if (RawConflict && !Dialogs.Confirm("The configuration was changed on another tab or page after you started editing the text. Applying replaces those changes with this text. Continue?"))
            return;
        IsRawModified = false;
        Session.ReplaceDocument(doc);
        _shell.SetStatus("Text applied to the editor. " + OperatorNotice.NotYetSaved);
    }

    // ------------------------------------------------------------------ (d) diagnostics

    public ObservableCollection<DiagnosticRow> GuiDiagnostics { get; } = [];

    public string DiagnosticsSummary
    {
        get
        {
            int e = GuiDiagnostics.Count(d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
            int w = GuiDiagnostics.Count(d => d.Diagnostic.Severity == DiagnosticSeverity.Warning);
            int i = GuiDiagnostics.Count - e - w;
            return GuiDiagnostics.Count == 0 ? "Dire Wolf Station's own checks found nothing to report." : $"{e} error(s), {w} warning(s), {i} note(s) from Dire Wolf Station's own checks.";
        }
    }

    private DiagnosticRow? _selectedDiagnostic;
    public DiagnosticRow? SelectedDiagnostic { get => _selectedDiagnostic; set => Set(ref _selectedDiagnostic, value); }

    public AsyncCommand CheckCommand { get; }

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; private set => Set(ref _isChecking, value); }

    private string _checkStatus = "Not checked yet. The real Dire Wolf parser runs with --check-config: it reads the file and opens no audio device, PTT or network port.";
    public string CheckStatus { get => _checkStatus; private set => Set(ref _checkStatus, value); }

    private string _checkStatusKind = "Neutral";
    public string CheckStatusKind { get => _checkStatusKind; private set => Set(ref _checkStatusKind, value); }

    public ObservableCollection<DiagnosticRow> CheckDiagnostics { get; } = [];
    public ObservableCollection<string> CheckNotes { get; } = [];

    private string _checkRawOutput = "";
    public string CheckRawOutput { get => _checkRawOutput; private set => Set(ref _checkRawOutput, value); }

    private string _checkSummaryText = "";
    public string CheckSummaryText { get => _checkSummaryText; private set => Set(ref _checkSummaryText, value); }

    private async Task CheckWithDireWolfAsync()
    {
        var doc = Session.Document;
        if (doc == null) return;
        string? exe = _shell.DireWolfPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            CheckStatus = "direwolf.exe is not selected (or not found), so the real parser cannot run. Choose it in Settings or the Setup wizard.";
            CheckStatusKind = "Warning";
            return;
        }
        IsChecking = true;
        CheckStatus = "Running direwolf --check-config…";
        CheckStatusKind = "Info";
        try
        {
            CheckConfigResult? result;
            string what;
            if (!Session.IsDirty && !Session.IsStale)
            {
                what = "the saved file";
                result = await _shell.CheckConfigAsync();
            }
            else
            {
                what = "your unsaved edits (a temporary copy)";
                string dir = AppPaths.Local;
                string temp = Path.Combine(dir, "unsaved-check-" + Guid.NewGuid().ToString("N")[..8] + ".conf");
                byte[] bytes = doc.ToBytes();
                try
                {
                    await Task.Run(() => { Directory.CreateDirectory(dir); File.WriteAllBytes(temp, bytes); });
                    result = await ConfigChecker.CheckFileAsync(exe, temp);
                }
                finally
                {
                    _ = Task.Run(() => { try { File.Delete(temp); } catch (Exception) { } });
                }
            }
            ShowCheckResult(result, what);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void ShowCheckResult(CheckConfigResult? r, string what)
    {
        CheckDiagnostics.Clear();
        CheckNotes.Clear();
        if (r == null)
        {
            CheckStatus = "The check did not run (no direwolf.exe or no configuration selected).";
            CheckStatusKind = "Warning";
            CheckRawOutput = "";
            CheckSummaryText = "";
            return;
        }
        CheckDiagnostics.ReplaceWith(r.Diagnostics.Select(d => new DiagnosticRow(d)));
        CheckNotes.ReplaceWith(r.Notes);
        CheckRawOutput = r.RawOutput;
        (CheckStatus, CheckStatusKind) = r.Status switch
        {
            CheckConfigStatus.Ok => ($"Dire Wolf's own parser accepted {what} without diagnostics.", "Success"),
            CheckConfigStatus.Diagnostics => ($"Dire Wolf's parser reported {r.Diagnostics.Count} diagnostic(s) for {what}.", "Warning"),
            CheckConfigStatus.Unsupported => ((r.Message ?? ConfigChecker.UnsupportedMessage) + " Dire Wolf 1.8.2 and earlier official builds do not have --check-config; the GUI's own checks above still apply.", "Warning"),
            _ => ("The real-parser check failed: " + (r.Message ?? "unknown reason") + (r.TimedOut ? " (timed out)" : ""), "Error"),
        };
        var s = r.Summary;
        CheckSummaryText = s == null ? "" :
            $"Version: {s.Version ?? "unknown"}.  Features: {(s.Features.Count == 0 ? "none reported" : string.Join(", ", s.Features))}.  " +
            $"Radio channels: {string.Join(", ", s.Channels.Where(c => c.Medium == ChannelMedium.Radio).Select(c => $"{c.Number} ({c.Modem ?? "?"}{(c.IsWapr ? " " + c.WaprProfile : "")}, {c.MyCall})"))}.  " +
            $"AGW port: {(s.AgwPort is int a ? a.ToString(CultureInfo.InvariantCulture) : "off")}.  KISS ports: {(s.KissPorts.Count == 0 ? "none" : string.Join(", ", s.KissPorts.Select(k => k.Port)))}.  " +
            $"Network: {(s.TcpBindReported ? (s.BindLocalOnly ? "this computer only" : "all interfaces") : "not reported by this build")}.";
    }

    // ------------------------------------------------------------------ (e) backups

    public ObservableCollection<BackupRow> Backups { get; } = [];

    private BackupRow? _selectedBackup;
    public BackupRow? SelectedBackup { get => _selectedBackup; set => Set(ref _selectedBackup, value); }

    public string BackupsNote =>
        $"Backups are kept in {_shell.Backups.BackupDirectory} (the newest {_shell.Backups.Keep} per file name). " +
        "They are matched by file name, so backups of other files with the same name appear here too.";

    public AsyncCommand RefreshBackupsCommand { get; }
    public AsyncCommand ViewBackupCommand { get; }
    public AsyncCommand RestoreBackupCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }

    private async Task RefreshBackupsAsync()
    {
        string? path = Session.Path ?? _shell.ConfigPath;
        if (path == null) { Backups.Clear(); return; }
        var list = await Task.Run(() => _shell.Backups.List(Path.GetFileName(path)));
        Backups.ReplaceWith(list.Select(b => new BackupRow(b)));
    }

    private async Task ViewBackupAsync(BackupRow? row)
    {
        if (row == null) return;
        string current = Session.Document?.ToText() ?? "";
        var backupDoc = await Task.Run(() => ConfigDocument.Load(row.Backup.Path));
        var secrets = ConfigChecker.FindSecrets(backupDoc).Concat(Session.Document == null ? [] : ConfigChecker.FindSecrets(Session.Document)).Distinct().ToList();
        var diff = TextDiff.Compute(ConfigChecker.Redact(backupDoc.ToText(), secrets), ConfigChecker.Redact(current, secrets));
        ConfigDiffDialog.Show("Backup compared with the editor", $"Backup of {row.When} (left, -) compared with the configuration in the editor (right, +):", diff,
            ["APRS-IS passcodes are shown as *****."], null, "backup " + row.When, "editor");
    }

    private async Task RestoreBackupAsync(BackupRow? row)
    {
        if (row == null || Session.Path == null) return;
        string target = Session.Path;
        var (backupText, currentText, secrets) = await Task.Run(() =>
        {
            var b = ConfigDocument.Load(row.Backup.Path);
            var c = File.Exists(target) ? ConfigDocument.Load(target) : new ConfigDocument();
            return (b.ToText(), c.ToText(), ConfigChecker.FindSecrets(b).Concat(ConfigChecker.FindSecrets(c)).Distinct().ToList());
        });
        var diff = TextDiff.Compute(ConfigChecker.Redact(currentText, secrets), ConfigChecker.Redact(backupText, secrets));
        var notes = new List<string>
        {
            $"The current file is backed up first, so this can be undone from this list.",
            "The restored file may transmit or forward differently from the current one (beacons, digipeater, IGate); review the Gateway & digipeater page afterwards.",
        };
        if (Session.IsDirty) notes.Insert(0, "Your unsaved edits in the editor are discarded.");
        if (_shell.IsDireWolfRunning) notes.Add("Dire Wolf is running: restart it to use the restored file.");
        if (!ConfigDiffDialog.Show("Restore backup?", $"Restore the backup of {row.When} over {target}? Changes:", diff, notes, "Restore", "current file", "after restoring"))
            return;
        await Task.Run(() => _shell.Backups.Restore(row.Backup.Path, target));
        IsRawModified = false;
        await Session.LoadAsync(target);
        if (_shell.IsDireWolfRunning) _shell.MarkRestartRequired();
        _shell.SetStatus($"Restored the backup of {row.When}.");
        await RefreshBackupsAsync();
    }

    // ------------------------------------------------------------------ (f) profiles

    public ObservableCollection<ProfileRow> Profiles { get; } = [];

    private ProfileRow? _selectedProfile;
    public ProfileRow? SelectedProfile { get => _selectedProfile; set => Set(ref _selectedProfile, value); }

    public string ProfilesNote => $"Profiles are named configuration files kept in {_shell.Profiles.ProfilesDirectory}. Opening one makes it the configuration Dire Wolf uses.";

    public AsyncCommand RefreshProfilesCommand { get; }
    public AsyncCommand SaveAsProfileCommand { get; }
    public AsyncCommand OpenProfileCommand { get; }
    public AsyncCommand DuplicateProfileCommand { get; }
    public AsyncCommand RenameProfileCommand { get; }
    public AsyncCommand ImportProfileCommand { get; }
    public AsyncCommand ExportProfileCommand { get; }
    public AsyncCommand DeleteProfileCommand { get; }
    public RelayCommand OpenProfilesFolderCommand { get; }

    private async Task RefreshProfilesAsync()
    {
        var list = await Task.Run(() => _shell.Profiles.List());
        string? current = _shell.ConfigPath;
        Profiles.ReplaceWith(list.Select(p => new ProfileRow(p, current != null && string.Equals(Path.GetFullPath(p.Path), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))));
    }

    private static string? AskName(string title, string prompt, string? initial)
    {
        var name = ConfigInputDialog.Ask(title, prompt, initial);
        if (name == null) return null;
        try { return ProfileManager.SanitizeName(name); }
        catch (ArgumentException e) { Dialogs.Warning(e.Message); return null; }
    }

    private async Task SaveAsProfileAsync()
    {
        var doc = Session.Document;
        if (doc == null) return;
        var name = AskName("Save as profile", "Profile name (the configuration in the editor, including unsaved edits, is saved under this name):",
            Path.GetFileNameWithoutExtension(Session.Path ?? "direwolf"));
        if (name == null) return;
        if (_shell.Profiles.Exists(name) && !Dialogs.Confirm($"A profile named \"{name}\" exists. Replace it? (A backup of it is made first.)")) return;
        await Task.Run(() =>
        {
            string p = _shell.Profiles.PathFor(name);
            if (File.Exists(p)) _shell.Backups.Backup(p);
            _shell.Profiles.Save(name, doc);
        });
        _shell.SetStatus($"Saved profile \"{name}\".");
        await RefreshProfilesAsync();
    }

    private async Task OpenProfileAsync(ProfileRow? row)
    {
        if (row == null) return;
        if (!await Session.ConfirmLeaveAsync("open the profile")) return;
        _shell.SelectConfig(row.Path);
        await Session.EnsureCurrentAsync();
        if (_shell.IsDireWolfRunning) _shell.MarkRestartRequired();
        _shell.SetStatus($"Profile \"{row.Name}\" is now the selected configuration.");
        await RefreshProfilesAsync();
    }

    private async Task DuplicateProfileAsync(ProfileRow? row)
    {
        if (row == null) return;
        var name = AskName("Duplicate profile", $"Name for the copy of \"{row.Name}\":", row.Name + " copy");
        if (name == null) return;
        await Task.Run(() => _shell.Profiles.Duplicate(row.Name, name));
        await RefreshProfilesAsync();
    }

    private async Task RenameProfileAsync(ProfileRow? row)
    {
        if (row == null) return;
        var name = AskName("Rename profile", $"New name for \"{row.Name}\":", row.Name);
        if (name == null || name == row.Name) return;
        bool wasCurrent = row.IsCurrent;
        if (wasCurrent && Session.IsDirty)
        {
            Dialogs.Info("Save or revert the unsaved changes to this profile first.");
            return;
        }
        string newPath = await Task.Run(() => _shell.Profiles.Rename(row.Name, name));
        if (wasCurrent)
        {
            _shell.SelectConfig(newPath);
            await Session.EnsureCurrentAsync();
        }
        await RefreshProfilesAsync();
    }

    private async Task ImportProfileAsync()
    {
        var src = Dialogs.OpenFile("Dire Wolf configuration (*.conf)|*.conf|All files (*.*)|*.*", null, "Import a configuration as a profile");
        if (src == null) return;
        var name = AskName("Import profile", "Profile name:", Path.GetFileNameWithoutExtension(src));
        if (name == null) return;
        bool overwrite = false;
        if (_shell.Profiles.Exists(name))
        {
            if (!Dialogs.Confirm($"A profile named \"{name}\" exists. Replace it? (A backup of it is made first.)")) return;
            overwrite = true;
        }
        await Task.Run(() =>
        {
            if (overwrite) _shell.Backups.Backup(_shell.Profiles.PathFor(name));
            _shell.Profiles.Import(src, name, overwrite);
        });
        await RefreshProfilesAsync();
    }

    private async Task ExportProfileAsync(ProfileRow? row)
    {
        if (row == null) return;
        var dest = Dialogs.SaveFile("Dire Wolf configuration (*.conf)|*.conf|All files (*.*)|*.*", row.Name + ".conf");
        if (dest == null) return;
        // The save dialog already asked before overwriting; keep a backup of what was there.
        await Task.Run(() =>
        {
            if (File.Exists(dest)) _shell.Backups.Backup(dest);
            _shell.Profiles.Export(row.Name, dest, overwrite: true);
        });
        _shell.SetStatus($"Exported profile \"{row.Name}\" to {dest}. It contains the APRS-IS passcode if the configuration has one.");
    }

    private async Task DeleteProfileAsync(ProfileRow? row)
    {
        if (row == null) return;
        string msg = $"Delete the profile \"{row.Name}\"?\n\nA copy is kept in the backups folder.";
        if (row.IsCurrent) msg += "\n\nIt is the selected configuration: Dire Wolf will have no configuration file until you open another one.";
        if (!Dialogs.Confirm(msg, "Delete profile", warning: true)) return;
        await Task.Run(() => _shell.Profiles.Delete(row.Name, _shell.Backups));
        await RefreshProfilesAsync();
    }
}
