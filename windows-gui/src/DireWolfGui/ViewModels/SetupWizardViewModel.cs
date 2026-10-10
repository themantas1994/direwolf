using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using DireWolfGui.Core.Config;
using DireWolfGui.Core.Process;
using DireWolfGui.Core.Wapr;
using DireWolfGui.Infrastructure;
using DireWolfGui.Views;

namespace DireWolfGui.ViewModels;

/// <summary>
/// First-run guide: direwolf.exe, configuration, audio (with a level meter that only listens),
/// channels, modem, PTT, callsign, client ports, validation and save, and a receive-only test run.
/// The wizard never tests transmitting.
/// </summary>
public sealed class SetupWizardViewModel : PageViewModel
{
    private const int MaxConsoleLines = 400;
    private readonly IShell _shell;
    private WaveInLevelMeter? _meter;
    private bool _testSubscribed;
    private CheckConfigSummary? _buildSummary;
    private string? _buildCheckMessage;

    public SetupWizardViewModel(IShell shell) : base("Setup wizard", "\uE82E")
    {
        _shell = shell;
        Steps =
        [
            new(1, "Dire Wolf program"), new(2, "Configuration file"), new(3, "Audio devices"), new(4, "Radio channels"),
            new(5, "Modem"), new(6, "Transmitter keying (PTT)"), new(7, "Callsign"), new(8, "Client applications"),
            new(9, "Check and save"), new(10, "Receive-only test"),
        ];
        Steps[0].IsCurrent = Steps[0].Visited = true;
        Channels.Add(new WizardChannel(0, () => WaprAllowed()));

        BackCommand = new RelayCommand(() => StepIndex--, () => StepIndex > 0);
        NextCommand = new AsyncCommand(NextAsync, () => StepIndex < Steps.Count - 1);
        OpenLinkCommand = new RelayCommand(p => OpenLink(p as string));

        BrowseExeCommand = new RelayCommand(BrowseExe);
        FindExeCommand = new RelayCommand(FindExe);
        DetectVersionCommand = new AsyncCommand(DetectVersionAsync, () => ExePath.Trim().Length > 0);

        BrowseExistingCommand = new RelayCommand(BrowseExisting);

        RefreshDevicesCommand = new AsyncCommand(RefreshDevicesAsync);
        StartMeterCommand = new RelayCommand(StartMeter, () => _meter == null && MeterDeviceNumber() != null);
        StopMeterCommand = new RelayCommand(StopMeter, () => _meter != null);

        CheckBuildCommand = new AsyncCommand(CheckBuildAsync, () => File.Exists(ExePath.Trim()));

        ValidateCommand = new AsyncCommand(ValidateAsync);
        BrowseSaveCommand = new RelayCommand(BrowseSave);
        SaveConfigCommand = new AsyncCommand(SaveConfigAsync);

        StartTestCommand = new AsyncCommand(StartTestAsync, () => !TestRunning);
        StopTestCommand = new AsyncCommand(StopTestAsync, () => TestRunning);
        FinishCommand = new RelayCommand(Finish);

        _shell.StationStateChanged += (_, _) =>
        {
            if (TestRunning && !_shell.IsDireWolfRunning)
            {
                TestRunning = false;
                TestStatus = $"Dire Wolf stopped. {PacketCount} packet(s) and {AudioLevelCount} audio level report(s) were seen.";
                UnsubscribeConsole();
            }
        };
    }

    // ------------------------------------------------------------------ steps

    public IReadOnlyList<WizardStep> Steps { get; }

    private int _stepIndex;
    public int StepIndex
    {
        get => _stepIndex;
        set
        {
            value = Math.Clamp(value, 0, Steps.Count - 1);
            if (!Set(ref _stepIndex, value)) return;
            for (int i = 0; i < Steps.Count; i++) Steps[i].IsCurrent = i == value;
            Steps[value].Visited = true;
            OnPropertyChanged(nameof(StepTitle));
            OnPropertyChanged(nameof(IsLastStep));
            if (value != 2) StopMeter();
            if (value == 2 && InputDevices.Count == 0 && OutputDevices.Count == 0) _ = RefreshDevicesAsync();
            if (value == 4 && _buildSummary == null && _buildCheckMessage == null && File.Exists(ExePath.Trim())) _ = CheckBuildAsync();
            if (value == 8) _ = ValidateAsync();
        }
    }

    public string StepTitle => $"Step {StepIndex + 1} of {Steps.Count}: {Steps[StepIndex].Title}";
    public bool IsLastStep => StepIndex == Steps.Count - 1;

    public RelayCommand BackCommand { get; }
    public AsyncCommand NextCommand { get; }
    public RelayCommand OpenLinkCommand { get; }

    private async Task NextAsync()
    {
        switch (StepIndex)
        {
            case 0:
                if (ExePath.Trim().Length > 0)
                {
                    if (!File.Exists(ExePath.Trim())) { Dialogs.Warning("That file does not exist.", ExePath); return; }
                    if (!string.Equals(_shell.DireWolfPath, ExePath.Trim(), StringComparison.OrdinalIgnoreCase)) _shell.SelectDireWolf(ExePath.Trim());
                    if (VersionInfo == null) await DetectVersionAsync();
                }
                break;
            case 1:
                if (UseExisting)
                {
                    if (!File.Exists(ExistingPath.Trim())) { Dialogs.Warning("Choose an existing configuration file, or select \"Create a new configuration\"."); return; }
                    if (!string.Equals(_shell.ConfigPath, ExistingPath.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        var session = ConfigurationSession.For(_shell);
                        if (!await session.ConfirmLeaveAsync("switch to that file")) return;
                        _shell.SelectConfig(ExistingPath.Trim());
                    }
                    StepIndex = 9; // the existing file is used as is; go to the receive-only test
                    return;
                }
                break;
            case 6:
                if (ConfigEdit.Callsign(Callsign) is { } err) { CallsignError = err; Dialogs.Warning(err); return; }
                break;
            case 7:
                if (ConfigEdit.WholeNumber(AgwPortText, 0, 65535, "The AGW port") is { } e1) { Dialogs.Warning(e1); return; }
                if (ConfigEdit.WholeNumber(KissPortText, 0, 65535, "The KISS port") is { } e2) { Dialogs.Warning(e2); return; }
                break;
        }
        StepIndex++;
    }

    private static void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) { Dialogs.Error("The link could not be opened.", e.Message); }
    }

    protected override void OnShown()
    {
        if (ExePath.Length == 0) ExePath = _shell.DireWolfPath ?? DireWolfProbe.FindExecutable() ?? "";
        if (ExistingPath.Length == 0 && _shell.ConfigPath is { } c) ExistingPath = c;
        OnPropertyChanged(nameof(FirstRunText));
    }

    protected override void OnHidden() => StopMeter();

    public string FirstRunText => _shell.Settings.FirstRunCompleted
        ? "Setup was completed before. You can run the wizard again to create another configuration; existing files are never overwritten without a backup and your confirmation."
        : "Welcome to Dire Wolf Station. These steps set up Dire Wolf to RECEIVE. Nothing here makes your station transmit: transmitting features stay off until you turn them on yourself.";

    // ------------------------------------------------------------------ 1 direwolf.exe

    private string _exePath = "";
    public string ExePath { get => _exePath; set { if (Set(ref _exePath, value ?? "")) { VersionInfo = null; VersionText = ""; } } }

    private DireWolfVersionInfo? _versionInfo;
    public DireWolfVersionInfo? VersionInfo { get => _versionInfo; private set => Set(ref _versionInfo, value); }

    private string _versionText = "";
    public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }

    private string _versionKind = "Neutral";
    public string VersionKind { get => _versionKind; private set => Set(ref _versionKind, value); }

    public RelayCommand BrowseExeCommand { get; }
    public RelayCommand FindExeCommand { get; }
    public AsyncCommand DetectVersionCommand { get; }

    public string DownloadUrl => "https://github.com/wb2osz/direwolf/releases";
    public string SourceUrl => "https://github.com/wb2osz/direwolf";

    private void BrowseExe()
    {
        var p = Dialogs.OpenFile("direwolf.exe|direwolf.exe|Programs (*.exe)|*.exe|All files (*.*)|*.*", ExePath, "Choose direwolf.exe");
        if (p != null) ExePath = p;
    }

    private void FindExe()
    {
        var found = DireWolfProbe.FindExecutable();
        if (found == null) { VersionText = "direwolf.exe was not found in the usual places (next to this program, C:\\direwolf, Program Files, PATH). Use Browse."; VersionKind = "Warning"; }
        else ExePath = found;
    }

    private async Task DetectVersionAsync()
    {
        string exe = ExePath.Trim();
        if (!File.Exists(exe)) { VersionText = "The file does not exist."; VersionKind = "Error"; return; }
        VersionText = "Asking Dire Wolf for its version (direwolf -t 0 -u)…";
        VersionKind = "Info";
        var info = await Task.Run(() => DireWolfProbe.ProbeAsync(exe, TimeSpan.FromSeconds(10)));
        VersionInfo = info;
        if (info == null)
        {
            VersionText = "No Dire Wolf version line was printed within 10 seconds. Is this really direwolf.exe? Version unknown.";
            VersionKind = "Warning";
            return;
        }
        VersionText = $"Dire Wolf {info.DisplayVersion}" + (info.Features.Count > 0 ? $"; optional support for: {string.Join(", ", info.Features)}" : "") + ".";
        VersionKind = "Success";
    }

    // ------------------------------------------------------------------ 2 configuration choice

    private bool _useExisting;
    public bool UseExisting { get => _useExisting; set { if (Set(ref _useExisting, value)) OnPropertyChanged(nameof(CreateNew)); } }
    public bool CreateNew { get => !_useExisting; set => UseExisting = !value; }

    private string _existingPath = "";
    public string ExistingPath { get => _existingPath; set => Set(ref _existingPath, value ?? ""); }

    public RelayCommand BrowseExistingCommand { get; }

    private void BrowseExisting()
    {
        var p = Dialogs.OpenFile("Dire Wolf configuration (*.conf)|*.conf|All files (*.*)|*.*", ExistingPath, "Choose an existing configuration");
        if (p != null) { ExistingPath = p; UseExisting = true; }
    }

    // ------------------------------------------------------------------ 3 audio

    public ObservableCollection<AudioDevice> InputDevices { get; } = [];
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];

    private AudioDevice? _selectedInput;
    public AudioDevice? SelectedInput
    {
        get => _selectedInput;
        set
        {
            if (!Set(ref _selectedInput, value) || value == null) return;
            InputText = WinMmAudio.AdeviceToken(value, InputDevices.ToList());
            if (_meter != null) { StopMeter(); StartMeter(); }
        }
    }

    private AudioDevice? _selectedOutput;
    public AudioDevice? SelectedOutput
    {
        get => _selectedOutput;
        set { if (Set(ref _selectedOutput, value) && value != null) OutputText = WinMmAudio.AdeviceToken(value, OutputDevices.ToList()); }
    }

    private string _inputText = "0";
    public string InputText { get => _inputText; set => Set(ref _inputText, value ?? ""); }
    private string _outputText = "0";
    public string OutputText { get => _outputText; set => Set(ref _outputText, value ?? ""); }

    public IReadOnlyList<string> SampleRates { get; } = ["Dire Wolf default (44100)", "44100", "48000"];
    private string _sampleRate = "Dire Wolf default (44100)";
    public string SampleRate { get => _sampleRate; set => Set(ref _sampleRate, value ?? SampleRates[0]); }

    private string _devicesNote = "";
    public string DevicesNote { get => _devicesNote; private set => Set(ref _devicesNote, value); }

    public AsyncCommand RefreshDevicesCommand { get; }
    public RelayCommand StartMeterCommand { get; }
    public RelayCommand StopMeterCommand { get; }

    private double _levelLeft, _levelRight, _peakHold;
    public double LevelLeft { get => _levelLeft; private set => Set(ref _levelLeft, value); }
    public double LevelRight { get => _levelRight; private set => Set(ref _levelRight, value); }

    private string _meterText = "The meter is off. It only listens to the input device; nothing is sent to the radio.";
    public string MeterText { get => _meterText; private set => Set(ref _meterText, value); }

    private string _meterKind = "Neutral";
    public string MeterKind { get => _meterKind; private set => Set(ref _meterKind, value); }

    public bool MeterRunning => _meter != null;

    private async Task RefreshDevicesAsync()
    {
        var (ins, outs) = await Task.Run(() => (WinMmAudio.InputDevices(), WinMmAudio.OutputDevices()));
        string? keepIn = SelectedInput?.Name, keepOut = SelectedOutput?.Name;
        InputDevices.ReplaceWith(ins);
        OutputDevices.ReplaceWith(outs);
        SelectedInput = InputDevices.FirstOrDefault(d => d.Name == keepIn);
        SelectedOutput = OutputDevices.FirstOrDefault(d => d.Name == keepOut);
        DevicesNote = ins.Count == 0 && outs.Count == 0
            ? "No sound devices were found (or this is not Windows). You can still type a device number or name."
            : $"{ins.Count} input and {outs.Count} output device(s), as Dire Wolf for Windows numbers them.";
    }

    private int? MeterDeviceNumber()
    {
        if (SelectedInput != null) return SelectedInput.Number;
        string t = InputText.Trim().Trim('"');
        if (int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return n;
        return InputDevices.FirstOrDefault(d => t.Length > 0 && d.Name.Contains(t, StringComparison.Ordinal))?.Number;
    }

    private void StartMeter()
    {
        if (MeterDeviceNumber() is not int dev) return;
        int channels = Math.Min(2, Math.Max(1, InputDevices.FirstOrDefault(d => d.Number == dev)?.Channels ?? 1));
        if (Stereo) channels = 2;
        try
        {
            _meter = new WaveInLevelMeter(dev, channels);
            _peakHold = 0;
            var dispatcher = Application.Current?.Dispatcher;
            _meter.Level += (l, r) => dispatcher?.BeginInvoke(() => OnLevel(l, r));
            MeterText = "Listening…";
            MeterKind = "Info";
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            _meter = null;
            MeterText = e.Message;
            MeterKind = "Error";
        }
        OnPropertyChanged(nameof(MeterRunning));
    }

    private void OnLevel(double left, double right)
    {
        if (_meter == null) return;
        LevelLeft = left;
        LevelRight = right;
        _peakHold = Math.Max(_peakHold * 0.97, Math.Max(left, right));
        (MeterText, MeterKind) = _peakHold >= 98
            ? ($"Peak {_peakHold:0} % of full scale: CLIPPING. Turn the receive level down (radio volume or Windows recording level).", "Error")
            : _peakHold < 5
                ? ($"Peak {_peakHold:0} % of full scale: very little audio. Check the cable, the radio volume and that the right device is selected.", "Warning")
                : ($"Peak {_peakHold:0} % of full scale.", "Success");
    }

    private void StopMeter()
    {
        if (_meter == null) return;
        _meter.Dispose();
        _meter = null;
        LevelLeft = LevelRight = 0;
        MeterText = "The meter is off.";
        MeterKind = "Neutral";
        OnPropertyChanged(nameof(MeterRunning));
    }

    // ------------------------------------------------------------------ 4/5/6 channels, modem, PTT

    private bool _stereo;
    public bool Stereo
    {
        get => _stereo;
        set
        {
            if (!Set(ref _stereo, value)) return;
            if (value && Channels.Count == 1) Channels.Add(new WizardChannel(1, () => WaprAllowed()));
            else if (!value) while (Channels.Count > 1) Channels.RemoveAt(Channels.Count - 1);
        }
    }

    public ObservableCollection<WizardChannel> Channels { get; } = [];

    public AsyncCommand CheckBuildCommand { get; }

    private string _waprState = "";
    public string WaprState { get => _waprState; private set => Set(ref _waprState, value); }

    public bool WaprAvailable => WaprSupport.IsSupported(_buildSummary ?? _shell.LastCheck?.Summary);

    private bool WaprAllowed()
    {
        if (WaprAvailable) return true;
        Dialogs.Info("WAPR cannot be selected.", WaprState);
        return false;
    }

    private void UpdateWaprState()
    {
        var summary = _buildSummary ?? _shell.LastCheck?.Summary;
        if (summary != null)
            WaprState = WaprSupport.IsSupported(summary, out var e) ? e + " It stays off unless you tick it for a channel." : e;
        else
            WaprState = _buildCheckMessage != null
                ? "WAPR is not available here: " + _buildCheckMessage + " Without the real-parser check the GUI cannot tell whether this build has WAPR, so the option stays disabled."
                : "Unknown whether this Dire Wolf build includes WAPR (it has not been checked yet). Use \"Check this Dire Wolf build\".";
        OnPropertyChanged(nameof(WaprAvailable));
    }

    /// <summary>Runs --check-config on a minimal receive-only configuration to learn the build's features (opens no devices).</summary>
    private async Task CheckBuildAsync()
    {
        string exe = ExePath.Trim();
        if (!File.Exists(exe)) { _buildCheckMessage = "direwolf.exe is not selected."; UpdateWaprState(); return; }
        WaprState = "Checking the Dire Wolf build…";
        string dir = AppPaths.Local;
        string temp = Path.Combine(dir, "wizard-build-check.conf");
        var text = ConfigTemplates.NewStation(ReceiveTestConfig.ReceiveOnlyOptions(new NewStationOptions { Callsign = "N0CALL" }));
        await Task.Run(() => { Directory.CreateDirectory(dir); AtomicFile.WriteAllText(temp, text); });
        var r = await ConfigChecker.CheckFileAsync(exe, temp);
        _buildSummary = r.Summary;
        _buildCheckMessage = r.Summary == null ? (r.Message ?? (r.Status == CheckConfigStatus.Unsupported ? ConfigChecker.UnsupportedMessage : "the check failed.")) : null;
        UpdateWaprState();
    }

    // ------------------------------------------------------------------ 7 callsign

    private string _callsign = "";
    public string Callsign
    {
        get => _callsign;
        set
        {
            if (!Set(ref _callsign, (value ?? "").ToUpperInvariant())) return;
            CallsignError = _callsign.Trim().Length == 0 ? null : ConfigEdit.Callsign(_callsign);
        }
    }

    private string? _callsignError;
    public string? CallsignError { get => _callsignError; private set => Set(ref _callsignError, value); }

    // ------------------------------------------------------------------ 8 network

    private string _agwPortText = "8000";
    public string AgwPortText { get => _agwPortText; set => Set(ref _agwPortText, value ?? ""); }
    private string _kissPortText = "8001";
    public string KissPortText { get => _kissPortText; set => Set(ref _kissPortText, value ?? ""); }

    private bool _allowRemote;
    public bool AllowRemoteClients
    {
        get => _allowRemote;
        set
        {
            if (value == _allowRemote) return;
            if (value && !Dialogs.Confirm(
                    "Allow programs on OTHER computers to connect to Dire Wolf's AGW and KISS ports (TCPBIND ANY)?\n\n" +
                    "Anything that connects can transmit with your callsign. Only use this on a trusted network, keep the ports closed in the firewall otherwise.\n\n" +
                    OperatorNotice.Responsibility, "Network access", warning: true))
            {
                OnPropertyChanged();
                return;
            }
            _allowRemote = value;
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------------ 9 validate and save

    public AsyncCommand ValidateCommand { get; }
    public RelayCommand BrowseSaveCommand { get; }
    public AsyncCommand SaveConfigCommand { get; }

    public ObservableCollection<DiagnosticRow> ValidationResults { get; } = [];

    private string _generatedText = "";
    public string GeneratedText { get => _generatedText; private set => Set(ref _generatedText, value); }

    private string _validationStatus = "";
    public string ValidationStatus { get => _validationStatus; private set => Set(ref _validationStatus, value); }

    private string _validationKind = "Neutral";
    public string ValidationKind { get => _validationKind; private set => Set(ref _validationKind, value); }

    private string _savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DireWolf", "direwolf.conf");
    public string SavePath { get => _savePath; set => Set(ref _savePath, value ?? ""); }

    private string _saveStatus = "";
    public string SaveStatus { get => _saveStatus; private set => Set(ref _saveStatus, value); }

    private bool _isValidating;

    public NewStationOptions BuildOptions()
    {
        int? rate = int.TryParse(SampleRate, NumberStyles.None, CultureInfo.InvariantCulture, out int r) ? r : null;
        return new NewStationOptions
        {
            Callsign = Callsign.Trim(),
            AudioDevices = [new AudioDeviceOption { Input = InputText.Trim().Trim('"'), Output = OutputText.Trim().Trim('"'), Stereo = Stereo, SampleRate = rate }],
            Channels = Channels.Select(c => c.ToOption()).ToList(),
            AgwPort = int.TryParse(AgwPortText.Trim(), out int a) ? a : 8000,
            KissPort = int.TryParse(KissPortText.Trim(), out int k) ? k : 8001,
            AllowRemoteClients = AllowRemoteClients,
            NewLine = OperatingSystem.IsWindows() ? "\r\n" : "\n",
        };
    }

    private string? Generate(NewStationOptions o, out string? error)
    {
        error = null;
        try { return ConfigTemplates.NewStation(o); }
        catch (ArgumentException e) { error = e.Message; return null; }
    }

    private async Task ValidateAsync()
    {
        if (_isValidating) return;
        _isValidating = true;
        try
        {
            ValidationResults.Clear();
            var text = Generate(BuildOptions(), out var error);
            if (text == null)
            {
                GeneratedText = "";
                ValidationStatus = "The configuration cannot be created yet: " + error;
                ValidationKind = "Error";
                return;
            }
            GeneratedText = text;
            var doc = ConfigDocument.Parse(text);
            var gui = ConfigValidator.Validate(doc).ToList();
            ValidationResults.ReplaceWith(gui.Select(d => new DiagnosticRow(d)));
            string exe = ExePath.Trim();
            if (!File.Exists(exe))
            {
                ValidationStatus = $"Dire Wolf Station's checks: {Count(gui)}. The real Dire Wolf parser was not run (direwolf.exe not selected).";
                ValidationKind = gui.Any(d => d.Severity == DiagnosticSeverity.Error) ? "Error" : "Warning";
                return;
            }
            ValidationStatus = "Running Dire Wolf's own parser (--check-config; opens no audio, PTT or network)…";
            ValidationKind = "Info";
            string dir = AppPaths.Local;
            string temp = Path.Combine(dir, "wizard-check.conf");
            await Task.Run(() => { Directory.CreateDirectory(dir); AtomicFile.WriteAllText(temp, text); });
            var r = await ConfigChecker.CheckFileAsync(exe, temp);
            foreach (var d in r.Diagnostics) ValidationResults.Add(new DiagnosticRow(d));
            if (r.Summary != null) { _buildSummary = r.Summary; UpdateWaprState(); }
            string real = r.Status switch
            {
                CheckConfigStatus.Ok => "Dire Wolf's parser accepted it.",
                CheckConfigStatus.Diagnostics => $"Dire Wolf's parser reported {r.Diagnostics.Count} diagnostic(s).",
                CheckConfigStatus.Unsupported => r.Message ?? ConfigChecker.UnsupportedMessage,
                _ => "Dire Wolf's parser could not be run: " + (r.Message ?? "unknown reason"),
            };
            bool errors = gui.Any(d => d.Severity == DiagnosticSeverity.Error) || r.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
            ValidationStatus = $"Dire Wolf Station's checks: {Count(gui)}. {real}";
            ValidationKind = errors ? "Error" : r.Status == CheckConfigStatus.Ok && gui.All(d => d.Severity == DiagnosticSeverity.Info) ? "Success" : "Warning";
        }
        finally
        {
            _isValidating = false;
        }
    }

    private static string Count(IReadOnlyCollection<ConfigDiagnostic> d)
    {
        int e = d.Count(x => x.Severity == DiagnosticSeverity.Error), w = d.Count(x => x.Severity == DiagnosticSeverity.Warning);
        return e + w == 0 ? "no problems" : $"{e} error(s), {w} warning(s)";
    }

    private void BrowseSave()
    {
        var p = Dialogs.SaveFile("Dire Wolf configuration (*.conf)|*.conf|All files (*.*)|*.*", Path.GetFileName(SavePath), SavePath);
        if (p != null) SavePath = p;
    }

    private async Task SaveConfigAsync()
    {
        var text = Generate(BuildOptions(), out var error);
        if (text == null) { Dialogs.Warning("The configuration cannot be created yet.", error); return; }
        string path = SavePath.Trim();
        if (path.Length == 0) { Dialogs.Warning("Choose where to save the configuration."); return; }
        try { path = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { Dialogs.Warning("That is not a valid file name.", e.Message); return; }
        if (ValidationResults.Any(d => d.Diagnostic.Severity == DiagnosticSeverity.Error) &&
            !Dialogs.Confirm("The checks reported errors. Save the configuration anyway?", "Errors found", warning: true)) return;

        string oldText = await Task.Run(() => File.Exists(path) ? ConfigDocument.Load(path).ToText() : "");
        bool exists = oldText.Length > 0 || File.Exists(path);
        var notes = new List<string>();
        if (exists)
        {
            notes.Add($"A file already exists there. It is copied to {_shell.Backups.BackupDirectory} before it is replaced.");
            if (oldText.Contains("IGLOGIN", StringComparison.OrdinalIgnoreCase)) notes.Add("Passcodes in the existing file are shown as *****.");
        }
        else notes.Add("A new file is created.");
        notes.Add("Nothing in this configuration transmits on its own: there are no beacons, no digipeater and no IGate. Client programs connected to the AGW/KISS ports can transmit once a PTT method is set.");
        var secrets = exists ? ConfigChecker.FindSecrets(ConfigDocument.Parse(oldText)) : [];
        var diff = TextDiff.Compute(ConfigChecker.Redact(oldText, secrets), text);
        if (!ConfigDiffDialog.Show(exists ? "Replace the existing configuration?" : "Save the new configuration?", path, diff, notes, exists ? "Back up and replace" : "Save",
                exists ? "existing file" : "(new file)", "new configuration"))
            return;

        await Task.Run(() =>
        {
            if (File.Exists(path)) _shell.Backups.Backup(path);
            AtomicFile.WriteAllBytes(path, ConfigDocument.Parse(text).ToBytes());
        });
        var session = ConfigurationSession.For(_shell);
        if (!await session.ConfirmLeaveAsync("switch to the new configuration")) { SaveStatus = $"Saved to {path}, but it is not selected because the editor has unsaved changes."; return; }
        _shell.SelectConfig(path);
        if (_shell.IsDireWolfRunning) _shell.MarkRestartRequired();
        ExistingPath = path;
        SaveStatus = $"Saved to {path} and selected as the configuration Dire Wolf uses.";
        _shell.SetStatus(SaveStatus);
    }

    // ------------------------------------------------------------------ 10 receive-only test

    public AsyncCommand StartTestCommand { get; }
    public AsyncCommand StopTestCommand { get; }
    public RelayCommand FinishCommand { get; }

    public ObservableCollection<string> ConsoleLines { get; } = [];

    private bool _testRunning;
    public bool TestRunning { get => _testRunning; private set => Set(ref _testRunning, value); }

    private int _packetCount;
    public int PacketCount { get => _packetCount; private set => Set(ref _packetCount, value); }

    private int _audioLevelCount;
    public int AudioLevelCount { get => _audioLevelCount; private set => Set(ref _audioLevelCount, value); }

    private string _lastAudioLevel = "not reported yet";
    public string LastAudioLevel { get => _lastAudioLevel; private set => Set(ref _lastAudioLevel, value); }

    private string _testStatus = "Not started.";
    public string TestStatus { get => _testStatus; private set => Set(ref _testStatus, value); }

    public string TestConfigPath => Path.Combine(AppPaths.Local, "receive-test.conf");

    private async Task StartTestAsync()
    {
        string exe = _shell.DireWolfPath ?? "";
        if (!File.Exists(exe)) { Dialogs.Warning("Choose direwolf.exe first (step 1)."); return; }

        ConfigDocument doc;
        if (UseExisting && File.Exists(ExistingPath.Trim()))
        {
            string src = ExistingPath.Trim();
            doc = await Task.Run(() => ConfigDocument.Load(src));
        }
        else
        {
            var text = Generate(ReceiveTestConfig.ReceiveOnlyOptions(BuildOptions()), out var error);
            if (text == null) { Dialogs.Warning("The test configuration cannot be created yet.", error); return; }
            doc = ConfigDocument.Parse(text);
        }
        ReceiveTestConfig.MakeReceiveOnly(doc);
        var problems = ReceiveTestConfig.Verify(doc);
        if (problems.Count > 0)
        {
            Dialogs.Error("The receive-only test configuration could still transmit, so the test was not started.", string.Join("\n", problems));
            return;
        }

        string path = TestConfigPath;
        await Task.Run(() => { Directory.CreateDirectory(AppPaths.Local); AtomicFile.WriteAllBytes(path, doc.ToBytes()); });

        if (_shell.IsDireWolfRunning)
        {
            if (!Dialogs.Confirm("Dire Wolf is already running. Stop it and start the receive-only test instead?")) return;
            await _shell.StopDireWolfAsync();
        }

        ConsoleLines.Clear();
        PacketCount = 0;
        AudioLevelCount = 0;
        LastAudioLevel = "not reported yet";
        SubscribeConsole();
        TestStatus = "Starting Dire Wolf with the receive-only test configuration…";
        try
        {
            await _shell.StartDireWolfAsync(path);
        }
        catch (Exception)
        {
            UnsubscribeConsole();
            throw;
        }
        TestRunning = _shell.IsDireWolfRunning;
        TestStatus = TestRunning
            ? "Receiving. Tune the radio to a busy packet / APRS frequency. Each decoded packet is counted below. AGW and KISS ports are off during the test, so no program can transmit."
            : "Dire Wolf did not keep running. See its output below (and the Logs page) for the reason.";
        if (!TestRunning) UnsubscribeConsole();
    }

    private async Task StopTestAsync()
    {
        await _shell.StopDireWolfAsync();
        TestRunning = false;
        UnsubscribeConsole();
        TestStatus = $"Stopped. {PacketCount} packet(s) and {AudioLevelCount} audio level report(s) were seen.";
    }

    private void SubscribeConsole()
    {
        if (_testSubscribed) return;
        _shell.ConsoleLine += OnConsoleLine;
        _testSubscribed = true;
    }

    private void UnsubscribeConsole()
    {
        if (!_testSubscribed) return;
        _shell.ConsoleLine -= OnConsoleLine;
        _testSubscribed = false;
    }

    private void OnConsoleLine(object? sender, string line)
    {
        ConsoleLines.Add(line);
        while (ConsoleLines.Count > MaxConsoleLines) ConsoleLines.RemoveAt(0);
        if (ReceiveTestLines.IsReceivedPacket(line)) PacketCount++;
        if (ReceiveTestLines.IsAudioLevel(line))
        {
            AudioLevelCount++;
            if (ReceiveTestLines.AudioLevelValue(line) is int v)
                LastAudioLevel = v.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void Finish()
    {
        _shell.Settings.FirstRunCompleted = true;
        _shell.SaveSettings();
        OnPropertyChanged(nameof(FirstRunText));
        _shell.SetStatus("Setup complete. Transmitting features (beacons, digipeater, IGate) are off until you enable them on the Gateway & digipeater page.");
    }
}
