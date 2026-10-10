using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using DireWolfGui.Core.Config;
using DireWolfGui.Core.Logging;
using DireWolfGui.Core.Net;
using DireWolfGui.Core.Process;
using DireWolfGui.Core.Settings;
using DireWolfGui.Core.Station;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>
/// The main window: owns settings, the station session (Dire Wolf process and its data),
/// the pages and the periodic UI refresh.  Implements <see cref="IShell"/> for the pages.
/// </summary>
public sealed class MainViewModel : ObservableObject, IShell, IAsyncDisposable
{
    private readonly SettingsStore _store = new();
    private readonly DispatcherTimer _timer;
    private readonly ConcurrentQueue<string> _pendingConsole = new();
    private int _tickCount;
    private PageViewModel? _selectedPage;
    private bool _restartRequired;
    private string _statusMessage = "Ready.";
    private string _resourceText = "";
    private CheckConfigResult? _lastCheck;
    private StationServicesReport? _services;
    private string? _activeConfig;          // configuration Dire Wolf was started with
    private string? _lastExitSummary;
    private GridLength _navWidth = new(210);

    public MainViewModel()
    {
        var load = _store.Load();
        Settings = load.Settings;
        if (load.CorruptCopyPath is not null)
            _statusMessage = $"Settings file was damaged and has been reset; the old copy is {load.CorruptCopyPath}.";

        Backups = new ConfigBackupManager(AppPaths.Backups);
        Profiles = new ProfileManager(AppPaths.Profiles);
        Session = new StationSession(logCapacity: Math.Max(1000, Settings.LogRetentionLines),
            packetCapacity: Math.Max(500, Settings.PacketRetentionCount));
        Session.Controller.OutputLine += (_, e) => _pendingConsole.Enqueue(SecretRedactor.Redact(e.Line));
        Session.Controller.StateChanged += (_, _) => Ui(OnStationStateChanged);
        Session.Controller.Exited += (_, e) => Ui(() => OnExited(e));
        ApplyMessageSettings();

        StartCommand = new AsyncCommand(() => StartDireWolfAsync(), () => !IsDireWolfBusy && !IsDireWolfRunning);
        StopCommand = new AsyncCommand(StopDireWolfAsync, () => IsDireWolfRunning);
        RestartCommand = new AsyncCommand(RestartDireWolfAsync, () => IsDireWolfRunning);
        NavigateCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out var i) && i >= 0 && i < Pages!.Count) SelectedPage = Pages[i];
        });

        Pages = new ObservableCollection<PageViewModel>(PageFactory.CreatePages(this));
        SelectedPage = Pages.FirstOrDefault(p => p.GetType().Name == Settings.LastWorkspacePage) ?? Pages[0];

        RefreshServices();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    // ---------------- IShell ----------------

    public AppSettings Settings { get; }
    public StationSession Session { get; }
    public ConfigBackupManager Backups { get; }
    public ProfileManager Profiles { get; }
    public string? ConfigPath => Settings.ConfigPath;
    public string? DireWolfPath => Settings.DireWolfExePath;
    public CheckConfigResult? LastCheck => _lastCheck;

    public bool IsDireWolfRunning => Session.State is DireWolfState.Running or DireWolfState.Starting;
    private bool IsDireWolfBusy => Session.State is DireWolfState.Starting or DireWolfState.Stopping;

    public event EventHandler<string>? ConsoleLine;
    public event EventHandler? StationStateChanged;

    public void SaveSettings()
    {
        try
        {
            _store.Save(Settings);
            ApplyMessageSettings();
        }
        catch (Exception ex)
        {
            Dialogs.Error("The settings could not be saved.", ex.Message);
        }
    }

    public void SelectConfig(string path)
    {
        Settings.ConfigPath = path;
        SaveSettings();
        _lastCheck = null;
        RefreshServices();
        RaiseStationChanged();
        _ = CheckConfigAsync();
    }

    public void SelectDireWolf(string path)
    {
        Settings.DireWolfExePath = path;
        SaveSettings();
        _lastCheck = null;
        RaiseStationChanged();
        _ = CheckConfigAsync();
    }

    public async Task<CheckConfigResult?> CheckConfigAsync()
    {
        if (!File.Exists(DireWolfPath) || !File.Exists(ConfigPath)) return null;
        try
        {
            _lastCheck = await ConfigChecker.CheckFileAsync(DireWolfPath!, ConfigPath!);
        }
        catch (Exception ex)
        {
            SetStatus("Configuration check failed: " + ex.Message);
            return null;
        }
        RefreshServices();
        RaiseStationChanged();
        return _lastCheck;
    }

    public async Task StartDireWolfAsync(string? configOverride = null)
    {
        if (IsDireWolfRunning) return;
        var exe = DireWolfPath;
        var config = configOverride ?? ConfigPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            Dialogs.Warning("Dire Wolf (direwolf.exe) has not been found.",
                "Choose it in Settings or run the Setup wizard. Dire Wolf is not part of this application; download or build it first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(config) || !File.Exists(config))
        {
            Dialogs.Warning("No Dire Wolf configuration file is selected.",
                "Open one on the Configuration page or create one with the Setup wizard.");
            return;
        }

        // Another Dire Wolf already using the sound card or ports would make this one fail.
        var others = await Task.Run(() => DireWolfProbe.FindRunningInstances());
        if (others.Any(o => !o.ManagedByThisProgram) &&
            !Dialogs.Confirm($"Another Dire Wolf is already running (process {string.Join(", ", others.Select(o => o.ProcessId))}), " +
                             "started outside this application. Two copies normally cannot share the same audio device or network ports.\n\nStart anyway?",
                "Dire Wolf already running", warning: true))
            return;

        // Validate with the real parser first when the build supports it.
        if (configOverride is null)
        {
            var check = await CheckConfigAsync();
            if (check is { Status: CheckConfigStatus.Diagnostics } &&
                check.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) &&
                !Dialogs.Confirm("Dire Wolf reported problems in the configuration:\n\n" +
                                 string.Join("\n", check.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Take(8).Select(d => "• " + d.Message)) +
                                 "\n\nStart anyway?", "Configuration problems", warning: true))
                return;
        }

        var doc = TryLoad(config);
        var facts = doc is null ? null : ConfigFacts.From(doc);
        var hasOwnLog = doc?.Lines.Any(l => l.Directive is "LOGDIR" or "LOGFILE") == true;
        var workDir = string.IsNullOrWhiteSpace(Settings.WorkingDirectory) ? Path.GetDirectoryName(exe)! : Settings.WorkingDirectory!;
        string? csvDir = null;
        if (hasOwnLog)
        {
            var logLine = doc!.Lines.First(l => l.Directive is "LOGDIR" or "LOGFILE");
            if (logLine.Directive == "LOGDIR" && logLine.Arguments.Count > 0)
                csvDir = Path.GetFullPath(Path.Combine(workDir, logLine.Arguments[0]));
        }
        else if (Settings.CsvLogEnabled)
        {
            csvDir = Settings.EffectiveCsvLogDirectory;
            Directory.CreateDirectory(csvDir);
        }

        var launch = new DireWolfLaunchOptions
        {
            ExecutablePath = exe,
            ConfigPath = config,
            WorkingDirectory = workDir,
            AudioStatsIntervalSeconds = Settings.AudioStatisticsInterval > 0 ? Settings.AudioStatisticsInterval : null,
            LogDirectory = !hasOwnLog && Settings.CsvLogEnabled ? csvDir : null,
        };
        var myCall = _lastCheck?.Summary?.Channel(0)?.MyCall;
        if (string.IsNullOrWhiteSpace(myCall)) myCall = facts?.Channels.Values.Select(c => c.MyCall).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        Directory.CreateDirectory(AppPaths.ConsoleLogs);
        var options = new StationSessionOptions
        {
            Launch = launch,
            ConnectAgwMonitor = Settings.AutoConnectMonitor,
            AgwHost = string.IsNullOrWhiteSpace(Settings.AgwHost) ? "127.0.0.1" : Settings.AgwHost,
            AgwPort = Settings.AgwPortOverride,
            CsvLogDirectory = csvDir,
            MyCall = myCall,
            SessionLogFile = Path.Combine(AppPaths.ConsoleLogs, "direwolf-console.log"),
        };

        try
        {
            SetStatus("Starting Dire Wolf…");
            _lastExitSummary = null;
            await Session.StartAsync(options);
            _activeConfig = config;
            RestartRequired = false;
            ApplyMessageSettings();
            SetStatus(configOverride is null ? "Dire Wolf started." : $"Dire Wolf started with {Path.GetFileName(config)}.");
        }
        catch (DireWolfStartException ex)
        {
            SetStatus("Dire Wolf did not start.");
            Dialogs.Error("Dire Wolf could not be started.", ex.Message);
        }
        RaiseStationChanged();
    }

    public async Task StopDireWolfAsync()
    {
        if (!IsDireWolfRunning) return;
        SetStatus("Stopping Dire Wolf…");
        var result = await Session.StopAsync();
        _activeConfig = null;
        RestartRequired = false;
        SetStatus(result.Message);
        if (result.PttMayBeKeyed)
            Dialogs.Warning("Dire Wolf did not stop by itself and had to be terminated.",
                "It could not release the transmitter itself. Check that your radio is not still transmitting (PTT keyed) and unkey it manually if needed.");
        RaiseStationChanged();
    }

    public async Task RestartDireWolfAsync()
    {
        var config = _activeConfig == ConfigPath ? null : _activeConfig;
        await StopDireWolfAsync();
        if (!IsDireWolfRunning) await StartDireWolfAsync(config);
    }

    public void MarkRestartRequired()
    {
        if (IsDireWolfRunning) RestartRequired = true;
        _lastCheck = null;
        RefreshServices();
        RaiseStationChanged();
    }

    public async Task<ConnectionCheck> TestConnectionAsync(string protocol, string host, int port)
    {
        var r = protocol.ToUpperInvariant() switch
        {
            "AGW" or "AGWPE" => await ConnectionTester.TestAgwAsync(host, port),
            "KISS" or "KISSTCP" or "KISS TCP" => await ConnectionTester.TestKissAsync(host, port),
            _ => await ConnectionTester.TestTcpAsync(host, port),
        };
        return new ConnectionCheck(r.Success, r.Summary, r.Detail);
    }

    public void Navigate<T>() where T : PageViewModel
    {
        var page = Pages.OfType<T>().FirstOrDefault();
        if (page is not null) SelectedPage = page;
    }

    public void SetStatus(string message) => StatusMessage = message;

    // ---------------- Bindable state ----------------

    public ObservableCollection<PageViewModel> Pages { get; }

    public PageViewModel? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedPage)) return;
            if (_selectedPage is not null) _selectedPage.IsActive = false;
            _selectedPage = value;
            value.IsActive = true;
            Settings.LastWorkspacePage = value.GetType().Name;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand RestartCommand { get; }
    public RelayCommand NavigateCommand { get; }

    public GridLength NavWidth
    {
        get => _navWidth;
        set => Set(ref _navWidth, value);
    }

    public string WindowTitle => $"{SelectedPage?.Title} — Dire Wolf Station";

    public string StationSubtitle
    {
        get
        {
            var call = _lastCheck?.Summary?.Channel(0)?.MyCall;
            var cfg = ConfigPath is null ? "no configuration selected" : Path.GetFileName(ConfigPath);
            return string.IsNullOrWhiteSpace(call) ? cfg : $"{call} · {cfg}";
        }
    }

    public string StateText => Session.State switch
    {
        DireWolfState.Running => "Running",
        DireWolfState.Starting => "Starting…",
        DireWolfState.Stopping => "Stopping…",
        DireWolfState.Failed => "Stopped unexpectedly",
        _ => "Stopped",
    };

    public string StateKind => Session.State switch
    {
        DireWolfState.Running => "Success",
        DireWolfState.Starting or DireWolfState.Stopping => "Warning",
        DireWolfState.Failed => "Error",
        _ => "Neutral",
    };

    public string StateDetail => _lastExitSummary ?? (IsDireWolfRunning
        ? $"Dire Wolf {Session.DireWolfVersion} with {Path.GetFileName(_activeConfig)}"
        : "Dire Wolf is not running. Press Start (F5).");

    public string UptimeText => Session.Uptime is TimeSpan t ? "up " + Converters.AgeConverter.Format(t) : "";

    public bool RestartRequired
    {
        get => _restartRequired;
        private set => Set(ref _restartRequired, value);
    }

    public string TransmitText => _services is null ? "TX: unknown"
        : _services.Services.Any(s => s.Transmits && s.Kind is not (StationServiceKind.ClientPort or StationServiceKind.SerialClient))
            ? "Transmits automatically"
            : "No automatic TX";

    public string TransmitKind => _services is null ? "Neutral"
        : _services.Services.Any(s => s.Transmits && s.Kind is not (StationServiceKind.ClientPort or StationServiceKind.SerialClient)) ? "Warning" : "Success";

    public string TransmitDetail => _services is null
        ? "Select a configuration to see what this station can transmit or forward."
        : _services.Services.Count == 0
            ? "Nothing in the configuration transmits or forwards traffic."
            : string.Join("\n", _services.Services.Select(s => "• " + s.Title + (s.Transmits ? " (transmits)" : "") + (s.Forwards ? " (forwards)" : "")))
              + "\n\nSee Gateway & digipeater for details.";

    public string MonitorText => !IsDireWolfRunning ? "Monitor: off" : Session.AgwMonitorReady ? "Monitor: AGW" : "Monitor: console";
    public string MonitorKind => !IsDireWolfRunning ? "Neutral" : Session.AgwMonitorReady ? "Success" : "Info";
    public string MonitorDetail => !IsDireWolfRunning
        ? "Packets are shown while Dire Wolf runs."
        : Session.AgwMonitorReady
            ? $"Packets come from Dire Wolf's console output; raw frames from its AGW interface on port {Session.AgwPort}."
            : "Packets come from Dire Wolf's console output. Raw frame bytes need the AGW interface (AGWPORT), which is not connected.";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string ResourceText
    {
        get => _resourceText;
        private set => Set(ref _resourceText, value);
    }

    public string TrafficText
    {
        get
        {
            var act = Session.ChannelActivity;
            return $"RX {act.Sum(a => a.Received)} · TX {act.Sum(a => a.Transmitted)}";
        }
    }

    public string AlertText
    {
        get
        {
            var n = Session.Alerts.GetLatest(200).Count(a => a.Time > DateTimeOffset.Now.AddMinutes(-30));
            return n == 0 ? "No recent alerts" : $"{n} alert{(n == 1 ? "" : "s")} in the last 30 min";
        }
    }

    public string AlertKind => Session.Alerts.GetLatest(50).Any(a => a.Severity == LogSeverity.Error && a.Time > DateTimeOffset.Now.AddMinutes(-30))
        ? "Error" : "SecondaryForeground";

    public string ClockText => $"{DateTime.Now:HH:mm:ss} local · {DateTime.UtcNow:HH:mm} UTC";

    public StationServicesReport? Services => _services;
    public string? LastExitSummary => _lastExitSummary;
    public string? ActiveConfig => _activeConfig;

    // ---------------- Internals ----------------

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private void OnTick()
    {
        _tickCount++;
        while (_pendingConsole.TryDequeue(out var line)) ConsoleLine?.Invoke(this, line);
        try
        {
            _selectedPage?.Tick();
            if (_tickCount % 4 == 0)
            {
                foreach (var p in Pages) p.BackgroundTick();
                OnPropertyChanged(nameof(UptimeText));
                OnPropertyChanged(nameof(ClockText));
                OnPropertyChanged(nameof(TrafficText));
                OnPropertyChanged(nameof(MonitorText));
                OnPropertyChanged(nameof(MonitorKind));
                OnPropertyChanged(nameof(MonitorDetail));
            }
            if (_tickCount % 8 == 0)
            {
                var r = Session.SampleResources();
                var own = System.Diagnostics.Process.GetCurrentProcess();
                ResourceText = (r is null ? "direwolf: —" : $"direwolf: CPU {r.CpuPercent:0.0}% · {r.WorkingSetBytes / 1048576.0:0} MB")
                               + $" · GUI {own.WorkingSet64 / 1048576.0:0} MB";
                OnPropertyChanged(nameof(AlertText));
                OnPropertyChanged(nameof(AlertKind));
            }
        }
        catch (Exception ex)
        {
            // A page refresh problem must not stop the refresh loop.
            CrashReporter.Write(ex, "refresh");
        }
    }

    private void OnStationStateChanged()
    {
        foreach (var name in new[] { nameof(StateText), nameof(StateKind), nameof(StateDetail), nameof(UptimeText), nameof(MonitorText), nameof(MonitorKind), nameof(MonitorDetail) })
            OnPropertyChanged(name);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        StationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnExited(Core.Process.DireWolfExitedEventArgs e)
    {
        if (e.Expected) return;
        _lastExitSummary = e.Diagnosis?.Summary ?? $"Dire Wolf exited unexpectedly (exit code {e.ExitCode}).";
        SetStatus(_lastExitSummary);
        Session.AddGuiLog(LogSeverity.Error, _lastExitSummary);
        OnStationStateChanged();
    }

    private void RaiseStationChanged()
    {
        OnPropertyChanged(nameof(StationSubtitle));
        OnStationStateChanged();
    }

    private void RefreshServices()
    {
        var doc = TryLoad(ConfigPath);
        _services = doc is null ? null : StationServicesAnalyzer.Analyze(doc, _lastCheck?.Summary);
        OnPropertyChanged(nameof(TransmitText));
        OnPropertyChanged(nameof(TransmitKind));
        OnPropertyChanged(nameof(TransmitDetail));
        OnPropertyChanged(nameof(Services));
        OnPropertyChanged(nameof(StationSubtitle));
    }

    private void ApplyMessageSettings()
    {
        var m = Session.Messages;
        // Automatic acknowledgements transmit, so they only happen when the user enabled messaging.
        m.AutoAcknowledge = Settings.MessagingEnabled && Settings.AutoAck;
        m.MaxTries = 1 + Math.Clamp(Settings.MessageRetryCount, 0, 10);   // first transmission + retries
        m.FirstRetryDelay = TimeSpan.FromSeconds(Math.Clamp(Settings.MessageRetrySeconds, 10, 600));
        var call = _lastCheck?.Summary?.Channel(0)?.MyCall;
        if (!string.IsNullOrWhiteSpace(call) && !call.Equals("N0CALL", StringComparison.OrdinalIgnoreCase)) m.MyCall = call;
    }

    private static ConfigDocument? TryLoad(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? ConfigDocument.Load(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Called when the window closes: stops Dire Wolf cleanly and saves settings.</summary>
    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        foreach (var p in Pages.OfType<IDisposable>()) p.Dispose();
        if (IsDireWolfRunning) await Session.StopAsync();
        await Session.DisposeAsync();
        try { _store.Save(Settings); } catch { /* best effort on exit */ }
    }
}
