using System.Collections.ObjectModel;
using System.Globalization;
using DireWolfGui.Core.Config;
using DireWolfGui.Core.Integrations;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>An external application profile being edited (written back to the settings on Save).</summary>
public sealed class ExternalAppItem : ObservableObject
{
    public ExternalAppItem(ExternalAppProfile profile)
    {
        Profile = profile;
        Load();
    }

    public ExternalAppProfile Profile { get; }
    public ExternalAppTemplate? Template => ExternalAppCatalog.Find(Profile.TemplateId);
    public IReadOnlyList<string> GuidanceSteps => Template?.SetupSteps ?? ["Custom application: configure it to use Dire Wolf's AGW port (AGWPE) or KISS TCP port shown on the right."];
    public string? Caveat => Template?.Caveat;
    public string TemplateName => Template?.Name ?? "Custom";

    private string _name = "", _exePath = "", _arguments = "", _workingDirectory = "", _host = "", _port = "", _serialPort = "", _notes = "", _configLocation = "";
    private TncProtocol _protocol;

    public string Name { get => _name; set => Edit(ref _name, value); }
    public string ExePath { get => _exePath; set => Edit(ref _exePath, value); }
    /// <summary>One argument per line.</summary>
    public string Arguments { get => _arguments; set => Edit(ref _arguments, value); }
    public string WorkingDirectory { get => _workingDirectory; set => Edit(ref _workingDirectory, value); }
    public TncProtocol Protocol { get => _protocol; set { if (Set(ref _protocol, value)) { Modified(); OnPropertyChanged(nameof(IsTcp)); OnPropertyChanged(nameof(IsSerial)); } } }
    public string Host { get => _host; set => Edit(ref _host, value); }
    public string Port { get => _port; set => Edit(ref _port, value); }
    public string SerialPort { get => _serialPort; set => Edit(ref _serialPort, value); }
    public string Notes { get => _notes; set => Edit(ref _notes, value); }
    public string ConfigLocation { get => _configLocation; set => Edit(ref _configLocation, value); }

    public bool IsTcp => Protocol is TncProtocol.Agwpe or TncProtocol.KissTcp;
    public bool IsSerial => Protocol == TncProtocol.SerialKiss;
    public string Summary => Profile.ConnectionSummary;

    private bool _isModified;
    public bool IsModified { get => _isModified; private set => Set(ref _isModified, value); }

    private string _result = "";
    public string Result { get => _result; set => Set(ref _result, value); }
    private string _resultKind = "Neutral";
    public string ResultKind { get => _resultKind; set => Set(ref _resultKind, value); }

    private void Edit(ref string field, string? value)
    {
        if (Set(ref field, value ?? "")) Modified();
    }

    private void Modified() => IsModified = true;

    public void Load()
    {
        _name = Profile.Name;
        _exePath = Profile.ExePath ?? "";
        _arguments = string.Join(Environment.NewLine, Profile.Arguments ?? []);
        _workingDirectory = Profile.WorkingDirectory ?? "";
        _protocol = Profile.Protocol;
        _host = Profile.Host;
        _port = Profile.Port.ToString(CultureInfo.InvariantCulture);
        _serialPort = Profile.SerialPort ?? "";
        _notes = Profile.Notes ?? "";
        _configLocation = Profile.ConfigLocation ?? "";
        IsModified = false;
        foreach (var n in new[] { nameof(Name), nameof(ExePath), nameof(Arguments), nameof(WorkingDirectory), nameof(Protocol), nameof(Host), nameof(Port),
                     nameof(SerialPort), nameof(Notes), nameof(ConfigLocation), nameof(IsTcp), nameof(IsSerial), nameof(Summary) })
            OnPropertyChanged(n);
    }

    /// <summary>Validate and copy the edits into the profile; returns an error or null.</summary>
    public string? Store()
    {
        if (Name.Trim().Length == 0) return "Give the application a name.";
        if (!int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
            return IsSerial ? "Enter the serial speed (baud), e.g. 9600." : "Enter a TCP port from 1 to 65535.";
        if (IsTcp && Host.Trim().Length == 0) return "Enter the host (127.0.0.1 for this computer).";
        Profile.Name = Name.Trim();
        Profile.ExePath = ExePath.Trim().Length == 0 ? null : ExePath.Trim().Trim('"');
        Profile.Arguments = Arguments.Replace("\r\n", "\n").Split('\n').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        Profile.WorkingDirectory = WorkingDirectory.Trim().Length == 0 ? null : WorkingDirectory.Trim().Trim('"');
        Profile.Protocol = Protocol;
        Profile.Host = Host.Trim().Length == 0 ? "127.0.0.1" : Host.Trim();
        Profile.Port = port;
        Profile.SerialPort = SerialPort.Trim().Length == 0 ? null : SerialPort.Trim();
        Profile.Notes = Notes.Trim().Length == 0 ? null : Notes.Trim();
        Profile.ConfigLocation = ConfigLocation.Trim().Length == 0 ? null : ConfigLocation.Trim();
        IsModified = false;
        OnPropertyChanged(nameof(Summary));
        return null;
    }
}

/// <summary>
/// External programs that use Dire Wolf as their TNC: templates with setup guidance, saved profiles
/// (launch, open folder, test the AGW/KISS connection) and the ports Dire Wolf actually offers.
/// </summary>
public sealed class IntegrationsViewModel : PageViewModel
{
    private readonly IShell _shell;

    public IntegrationsViewModel(IShell shell) : base("External programs", "")
    {
        _shell = shell;
        Apps = new ObservableCollection<ExternalAppItem>(_shell.Settings.ExternalApps.Select(a => new ExternalAppItem(a)));
        SelectedTemplate = Templates.FirstOrDefault();
        SelectedApp = Apps.FirstOrDefault();

        AddFromTemplateCommand = new RelayCommand(AddFromTemplate, () => SelectedTemplate != null);
        SaveAppCommand = new RelayCommand(SaveApp, () => SelectedApp is { IsModified: true });
        DiscardAppCommand = new RelayCommand(() => SelectedApp?.Load(), () => SelectedApp is { IsModified: true });
        RemoveAppCommand = new RelayCommand(RemoveApp, () => SelectedApp != null);
        LaunchCommand = new RelayCommand(Launch, () => SelectedApp != null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => SelectedApp != null);
        BrowseExeCommand = new RelayCommand(BrowseExe, () => SelectedApp != null);
        BrowseWorkDirCommand = new RelayCommand(BrowseWorkDir, () => SelectedApp != null);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync, () => SelectedApp is { IsTcp: true });
        CheckNowCommand = new AsyncCommand(async () => { await _shell.CheckConfigAsync(); UpdatePorts(); },
            () => !string.IsNullOrWhiteSpace(_shell.ConfigPath) && !string.IsNullOrWhiteSpace(_shell.DireWolfPath));
        _shell.StationStateChanged += (_, _) => { if (IsActive) UpdatePorts(); };
    }

    public string Disclaimer =>
        "Dire Wolf is the sound-card modem and TNC. BBS, Winlink, node, DX cluster, mapping and messaging functions come from the external application itself, not from Dire Wolf. " +
        "Any program connected to Dire Wolf can transmit with your callsign; decide in that program whether it may.";

    protected override void OnShown() => UpdatePorts();

    // ------------------------------------------------------------------ templates

    public IReadOnlyList<ExternalAppTemplate> Templates => ExternalAppCatalog.All;

    private ExternalAppTemplate? _selectedTemplate;
    public ExternalAppTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set { if (Set(ref _selectedTemplate, value)) OnPropertyChanged(nameof(TemplateSteps)); }
    }

    public IReadOnlyList<string> TemplateSteps => SelectedTemplate?.SetupSteps ?? [];

    public RelayCommand AddFromTemplateCommand { get; }

    private void AddFromTemplate()
    {
        if (SelectedTemplate == null) return;
        var profile = SelectedTemplate.CreateProfile();
        _shell.Settings.ExternalApps.Add(profile);
        _shell.SaveSettings();
        var item = new ExternalAppItem(profile);
        Apps.Add(item);
        SelectedApp = item;
        _shell.SetStatus($"Added {profile.Name}. Choose its program file to be able to launch it.");
    }

    // ------------------------------------------------------------------ saved applications

    public ObservableCollection<ExternalAppItem> Apps { get; }
    public IReadOnlyList<TncProtocol> Protocols { get; } = Enum.GetValues<TncProtocol>();

    private ExternalAppItem? _selectedApp;
    public ExternalAppItem? SelectedApp { get => _selectedApp; set { if (Set(ref _selectedApp, value)) OnPropertyChanged(nameof(HasSelectedApp)); } }
    public bool HasSelectedApp => SelectedApp != null;

    public RelayCommand SaveAppCommand { get; }
    public RelayCommand DiscardAppCommand { get; }
    public RelayCommand RemoveAppCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand BrowseExeCommand { get; }
    public RelayCommand BrowseWorkDirCommand { get; }
    public AsyncCommand TestConnectionCommand { get; }

    private void SaveApp()
    {
        if (SelectedApp is not { } app) return;
        if (app.Store() is { } err) { Dialogs.Warning(err); return; }
        _shell.SaveSettings();
        _shell.SetStatus($"Saved {app.Name}.");
    }

    private void RemoveApp()
    {
        if (SelectedApp is not { } app) return;
        if (!Dialogs.Confirm($"Remove \"{app.Name}\" from this list?\n\nThe program itself and its own settings are not touched.")) return;
        _shell.Settings.ExternalApps.Remove(app.Profile);
        _shell.SaveSettings();
        Apps.Remove(app);
        SelectedApp = Apps.FirstOrDefault();
    }

    private void Launch()
    {
        if (SelectedApp is not { } app) return;
        if (app.IsModified)
        {
            if (app.Store() is { } err) { Dialogs.Warning(err); return; }
            _shell.SaveSettings();
        }
        var r = ExternalAppLauncher.Launch(app.Profile);
        app.Result = r.Success ? $"Started {app.Name} (process {r.ProcessId})." : r.Error ?? "The program did not start.";
        app.ResultKind = r.Success ? "Success" : "Error";
    }

    private void OpenFolder()
    {
        if (SelectedApp is not { } app) return;
        string? dir = app.WorkingDirectory.Trim().Length > 0 ? app.WorkingDirectory.Trim()
            : app.ExePath.Trim().Length > 0 ? Path.GetDirectoryName(app.ExePath.Trim().Trim('"'))
            : app.ConfigLocation.Trim().Length > 0 ? app.ConfigLocation.Trim() : null;
        if (string.IsNullOrWhiteSpace(dir)) { Dialogs.Info("No folder is known for this program yet: choose its program file first."); return; }
        var r = ExternalAppLauncher.OpenFolder(dir);
        if (!r.Success) Dialogs.Error("The folder could not be opened.", r.Error);
    }

    private void BrowseExe()
    {
        if (SelectedApp is not { } app) return;
        var p = Dialogs.OpenFile("Programs (*.exe;*.bat;*.cmd;*.jar)|*.exe;*.bat;*.cmd;*.jar|All files (*.*)|*.*", app.ExePath, $"Choose the program for {app.Name}");
        if (p != null) app.ExePath = p;
    }

    private void BrowseWorkDir()
    {
        if (SelectedApp is not { } app) return;
        var p = Dialogs.PickFolder(app.WorkingDirectory.Length > 0 ? app.WorkingDirectory : Path.GetDirectoryName(app.ExePath), "Working folder");
        if (p != null) app.WorkingDirectory = p;
    }

    private async Task TestConnectionAsync()
    {
        if (SelectedApp is not { } app) return;
        if (!int.TryParse(app.Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            app.Result = "Enter a TCP port from 1 to 65535.";
            app.ResultKind = "Warning";
            return;
        }
        string protocol = app.Protocol == TncProtocol.Agwpe ? "AGWPE" : "KISS";
        app.Result = $"Connecting to {app.Host.Trim()}:{port} ({protocol})…";
        app.ResultKind = "Info";
        var r = await _shell.TestConnectionAsync(protocol, app.Host.Trim().Length == 0 ? "127.0.0.1" : app.Host.Trim(), port);
        app.Result = r.Summary + (string.IsNullOrWhiteSpace(r.Detail) ? "" : "\n" + r.Detail);
        app.ResultKind = r.Success ? "Success" : "Error";
    }

    // ------------------------------------------------------------------ Dire Wolf ports

    private string _agwText = "", _kissText = "", _bindText = "", _portsNote = "";
    public string AgwText { get => _agwText; private set => Set(ref _agwText, value); }
    public string KissText { get => _kissText; private set => Set(ref _kissText, value); }
    public string BindText { get => _bindText; private set => Set(ref _bindText, value); }
    public string PortsNote { get => _portsNote; private set => Set(ref _portsNote, value); }
    public AsyncCommand CheckNowCommand { get; }

    private void UpdatePorts()
    {
        var r = _shell.LastCheck;
        var s = r?.Summary;
        if (s == null)
        {
            AgwText = KissText = BindText = "unknown";
            PortsNote = r == null
                ? "The selected configuration has not been checked yet. \"Check now\" runs direwolf --check-config (opens no audio, PTT or network) to read the ports from Dire Wolf itself."
                : r.Status == CheckConfigStatus.Unsupported
                    ? "This Dire Wolf build cannot report its ports (no --check-config). Look in the configuration (AGWPORT, KISSPORT, TCPBIND) or in the log when Dire Wolf starts; the defaults are AGW 8000 and KISS 8001."
                    : "The check failed" + (r.Message != null ? ": " + r.Message : ".");
            return;
        }
        AgwText = s.AgwPort is int a ? a.ToString(CultureInfo.InvariantCulture) : "off (AGWPORT 0)";
        KissText = s.KissPorts.Count == 0 ? "off (KISSPORT 0)"
            : string.Join(", ", s.KissPorts.Select(k => k.Port.ToString(CultureInfo.InvariantCulture) + (k.Channel >= 0 ? $" (channel {k.Channel})" : "")));
        BindText = !s.TcpBindReported ? "not reported by this Dire Wolf build"
            : s.BindLocalOnly ? "this computer only (TCPBIND LOCAL): use host 127.0.0.1"
            : "all network interfaces (TCPBIND ANY): other computers can connect too";
        PortsNote = "From Dire Wolf's own parser for the saved configuration" + (s.Version != null ? $" (Dire Wolf {s.Version})" : "") + ". Type these into the other program.";
    }
}
