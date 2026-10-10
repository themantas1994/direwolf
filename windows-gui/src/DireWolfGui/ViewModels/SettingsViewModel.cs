using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DireWolfGui.Core.Integrations;
using DireWolfGui.Core.Settings;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>
/// Every application setting with an editor, Save / Discard / Reset to defaults, and About.
/// Settings that make the station transmit (messaging, automatic acknowledgements) ask first.
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    private readonly IShell _shell;
    private bool _loading;

    public SettingsViewModel(IShell shell) : base("Settings", "\uE713")
    {
        _shell = shell;
        SaveCommand = new AsyncCommand(SaveAsync, () => IsModified);
        DiscardCommand = new RelayCommand(Discard, () => IsModified);
        ResetCommand = new RelayCommand(Reset);
        BrowseExeCommand = new RelayCommand(() => { var p = Dialogs.OpenFile("direwolf.exe|direwolf.exe|Programs (*.exe)|*.exe|All files (*.*)|*.*", DireWolfExePath, "Choose direwolf.exe"); if (p != null) DireWolfExePath = p; });
        BrowseConfigCommand = new RelayCommand(() => { var p = Dialogs.OpenFile("Dire Wolf configuration (*.conf)|*.conf|All files (*.*)|*.*", ConfigPath, "Choose the configuration"); if (p != null) ConfigPath = p; });
        BrowseWorkDirCommand = new RelayCommand(() => { var p = Dialogs.PickFolder(WorkingDirectory, "Folder Dire Wolf runs in"); if (p != null) WorkingDirectory = p; });
        BrowseCsvDirCommand = new RelayCommand(() => { var p = Dialogs.PickFolder(CsvLogDirectory.Length > 0 ? CsvLogDirectory : AppSettings.DefaultCsvLogDirectory, "Folder for Dire Wolf's CSV packet logs"); if (p != null) CsvLogDirectory = p; });
        BrowseTileDirCommand = new RelayCommand(() => { var p = Dialogs.PickFolder(MapTileCacheDirectory, "Map tile cache folder"); if (p != null) MapTileCacheDirectory = p; });
        OpenFolderCommand = new RelayCommand(p => { if (p is string dir) OpenFolder(dir); });
        OpenLinkCommand = new RelayCommand(p => OpenLink(p as string));
        RunWizardCommand = new RelayCommand(() => _shell.Navigate<SetupWizardViewModel>());
        Load(_shell.Settings);
    }

    protected override void OnShown()
    {
        if (!IsModified) Load(_shell.Settings);
        OnPropertyChanged(nameof(DireWolfVersionText));
    }

    public AsyncCommand SaveCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand BrowseExeCommand { get; }
    public RelayCommand BrowseConfigCommand { get; }
    public RelayCommand BrowseWorkDirCommand { get; }
    public RelayCommand BrowseCsvDirCommand { get; }
    public RelayCommand BrowseTileDirCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
    public RelayCommand RunWizardCommand { get; }

    private bool _isModified;
    public bool IsModified { get => _isModified; private set => Set(ref _isModified, value); }

    private void Changed<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
        if (!_loading) IsModified = true;
    }

    // ------------------------------------------------------------------ fields

    private string _exe = "", _config = "", _workDir = "", _audioStats = "0", _csvDir = "", _logLines = "", _packets = "", _agwHost = "", _agwPort = "",
        _retryCount = "", _retrySeconds = "", _tileUrl = "", _attribution = "", _tileDir = "", _homeLat = "", _homeLon = "";
    private AppTheme _theme;
    private bool _csvEnabled, _autoConnect, _messaging, _autoAck, _tiles, _firstRun;

    public string DireWolfExePath { get => _exe; set => Changed(ref _exe, value ?? "", nameof(DireWolfExePath)); }
    public string ConfigPath { get => _config; set => Changed(ref _config, value ?? "", nameof(ConfigPath)); }
    public string WorkingDirectory { get => _workDir; set => Changed(ref _workDir, value ?? "", nameof(WorkingDirectory)); }
    public string AudioStatisticsInterval { get => _audioStats; set => Changed(ref _audioStats, value ?? "", nameof(AudioStatisticsInterval)); }

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();
    public AppTheme Theme
    {
        get => _theme;
        set
        {
            Changed(ref _theme, value, nameof(Theme));
            if (!_loading) ThemeManager.Apply(value.ToString());
        }
    }

    public bool CsvLogEnabled { get => _csvEnabled; set => Changed(ref _csvEnabled, value, nameof(CsvLogEnabled)); }
    public string CsvLogDirectory { get => _csvDir; set => Changed(ref _csvDir, value ?? "", nameof(CsvLogDirectory)); }
    public string DefaultCsvDirectory => AppSettings.DefaultCsvLogDirectory;
    public string LogRetentionLines { get => _logLines; set => Changed(ref _logLines, value ?? "", nameof(LogRetentionLines)); }
    public string PacketRetentionCount { get => _packets; set => Changed(ref _packets, value ?? "", nameof(PacketRetentionCount)); }

    public string AgwHost { get => _agwHost; set => Changed(ref _agwHost, value ?? "", nameof(AgwHost)); }
    public string AgwPortOverride { get => _agwPort; set => Changed(ref _agwPort, value ?? "", nameof(AgwPortOverride)); }
    public bool AutoConnectMonitor { get => _autoConnect; set => Changed(ref _autoConnect, value, nameof(AutoConnectMonitor)); }

    public bool MessagingEnabled
    {
        get => _messaging;
        set
        {
            if (value && !_messaging && !_loading && !Dialogs.Confirm(
                    "Enable APRS messaging?\n\nMessages you write are TRANSMITTED through Dire Wolf with your callsign, and unacknowledged messages are sent again automatically " +
                    "(retries below).\n\n" + OperatorNotice.Responsibility, "Messaging transmits", warning: true))
            {
                OnPropertyChanged();
                return;
            }
            Changed(ref _messaging, value, nameof(MessagingEnabled));
        }
    }

    public bool AutoAck
    {
        get => _autoAck;
        set
        {
            if (value && !_autoAck && !_loading && !Dialogs.Confirm(
                    "Acknowledge received messages automatically?\n\nWhenever a message addressed to your callsign is received, an acknowledgement is TRANSMITTED automatically, " +
                    "without you doing anything (only while messaging is enabled).\n\n" + OperatorNotice.Responsibility, "Automatic acknowledgements transmit", warning: true))
            {
                OnPropertyChanged();
                return;
            }
            Changed(ref _autoAck, value, nameof(AutoAck));
        }
    }

    public string MessageRetryCount { get => _retryCount; set => Changed(ref _retryCount, value ?? "", nameof(MessageRetryCount)); }
    public string MessageRetrySeconds { get => _retrySeconds; set => Changed(ref _retrySeconds, value ?? "", nameof(MessageRetrySeconds)); }

    public bool MapTilesEnabled
    {
        get => _tiles;
        set
        {
            if (value && !_tiles && !_loading && !Dialogs.Confirm(
                    "Download map tiles from the Internet?\n\nThe map then requests tiles from the server in the URL template below (by default the OpenStreetMap " +
                    "tile servers), which sees your IP address and the areas you view. Tiles are cached on disk and requested sparingly. The OpenStreetMap tile " +
                    "usage policy does not allow heavy use; use another provider for that. Map data © OpenStreetMap contributors (ODbL).", "Map tiles"))
            {
                OnPropertyChanged();
                return;
            }
            Changed(ref _tiles, value, nameof(MapTilesEnabled));
        }
    }

    public string MapTileUrlTemplate { get => _tileUrl; set => Changed(ref _tileUrl, value ?? "", nameof(MapTileUrlTemplate)); }
    public string MapAttribution { get => _attribution; set => Changed(ref _attribution, value ?? "", nameof(MapAttribution)); }
    public string MapTileCacheDirectory { get => _tileDir; set => Changed(ref _tileDir, value ?? "", nameof(MapTileCacheDirectory)); }
    public string DefaultTileDirectory => Path.Combine(AppSettings.LocalDataDirectory, "tiles");
    public string HomeLatitude { get => _homeLat; set => Changed(ref _homeLat, value ?? "", nameof(HomeLatitude)); }
    public string HomeLongitude { get => _homeLon; set => Changed(ref _homeLon, value ?? "", nameof(HomeLongitude)); }

    public bool FirstRunCompleted { get => _firstRun; set => Changed(ref _firstRun, value, nameof(FirstRunCompleted)); }

    public string RoamingFolder => AppPaths.Roaming;
    public string LocalFolder => AppPaths.Local;

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string N(double? v) => v is double d ? d.ToString("0.######", CultureInfo.InvariantCulture) : "";

    private void Load(AppSettings s)
    {
        _loading = true;
        try
        {
            DireWolfExePath = s.DireWolfExePath ?? "";
            ConfigPath = s.ConfigPath ?? "";
            WorkingDirectory = s.WorkingDirectory ?? "";
            AudioStatisticsInterval = N(s.AudioStatisticsInterval);
            Theme = s.Theme;
            CsvLogEnabled = s.CsvLogEnabled;
            CsvLogDirectory = s.CsvLogDirectory ?? "";
            LogRetentionLines = N(s.LogRetentionLines);
            PacketRetentionCount = N(s.PacketRetentionCount);
            AgwHost = s.AgwHost;
            AgwPortOverride = s.AgwPortOverride is int p ? N(p) : "";
            AutoConnectMonitor = s.AutoConnectMonitor;
            MessagingEnabled = s.MessagingEnabled;
            AutoAck = s.AutoAck;
            MessageRetryCount = N(s.MessageRetryCount);
            MessageRetrySeconds = N(s.MessageRetrySeconds);
            MapTilesEnabled = s.MapTilesEnabled;
            MapTileUrlTemplate = s.MapTileUrlTemplate;
            MapAttribution = s.MapAttribution;
            MapTileCacheDirectory = s.MapTileCacheDirectory ?? "";
            HomeLatitude = N(s.HomeLatitude);
            HomeLongitude = N(s.HomeLongitude);
            FirstRunCompleted = s.FirstRunCompleted;
        }
        finally
        {
            _loading = false;
        }
        IsModified = false;
    }

    private static bool TryInt(string s, int min, int max, out int v) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;

    private static bool TryCoord(string s, double limit, out double? v)
    {
        v = null;
        if (s.Trim().Length == 0) return true;
        if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d < -limit || d > limit) return false;
        v = d;
        return true;
    }

    private string? Validate(out Action<AppSettings>? apply)
    {
        apply = null;
        if (!TryInt(AudioStatisticsInterval, 0, 3600, out int stats)) return "Audio statistics interval: a whole number of seconds from 0 (off) to 3600.";
        if (!TryInt(LogRetentionLines, 100, 1_000_000, out int lines)) return "Log lines kept: 100 to 1,000,000.";
        if (!TryInt(PacketRetentionCount, 100, 1_000_000, out int packets)) return "Packets kept: 100 to 1,000,000.";
        int? agwPort = null;
        if (AgwPortOverride.Trim().Length > 0)
        {
            if (!TryInt(AgwPortOverride, 1, 65535, out int ap)) return "AGW port: 1 to 65535, or empty to use the one from the configuration.";
            agwPort = ap;
        }
        if (!TryInt(MessageRetryCount, 0, 20, out int retries)) return "Message retries: 0 to 20.";
        if (!TryInt(MessageRetrySeconds, 5, 3600, out int retrySec)) return "Seconds before the first retry: 5 to 3600.";
        if (!TryCoord(HomeLatitude, 90, out double? lat)) return "Home latitude: decimal degrees from -90 to 90 (south negative), with a dot, e.g. 42.6190.";
        if (!TryCoord(HomeLongitude, 180, out double? lon)) return "Home longitude: decimal degrees from -180 to 180 (west negative), with a dot.";
        if ((lat == null) != (lon == null)) return "Enter both home latitude and longitude, or neither.";
        if (MapTileUrlTemplate.Trim().Length > 0 && !(MapTileUrlTemplate.Contains("{z}") && MapTileUrlTemplate.Contains("{x}") && MapTileUrlTemplate.Contains("{y}")))
            return "The tile URL template must contain {z}, {x} and {y}.";
        if (MapTileUrlTemplate.Trim().Length > 0 && !MapTileUrlTemplate.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !MapTileUrlTemplate.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "The tile URL template must start with https://.";
        if (AgwHost.Trim().Length == 0) return "AGW host: e.g. 127.0.0.1.";
        string csvDir = CsvLogDirectory.Trim(), tileDir = MapTileCacheDirectory.Trim(), workDir = WorkingDirectory.Trim();
        if (workDir.Length > 0 && !Directory.Exists(workDir)) return $"The working folder does not exist: {workDir}";

        apply = s =>
        {
            s.WorkingDirectory = workDir.Length == 0 ? null : workDir;
            s.AudioStatisticsInterval = stats;
            s.Theme = Theme;
            s.CsvLogEnabled = CsvLogEnabled;
            s.CsvLogDirectory = csvDir.Length == 0 ? null : csvDir;
            s.LogRetentionLines = lines;
            s.PacketRetentionCount = packets;
            s.AgwHost = AgwHost.Trim();
            s.AgwPortOverride = agwPort;
            s.AutoConnectMonitor = AutoConnectMonitor;
            s.MessagingEnabled = MessagingEnabled;
            s.AutoAck = AutoAck;
            s.MessageRetryCount = retries;
            s.MessageRetrySeconds = retrySec;
            s.MapTilesEnabled = MapTilesEnabled;
            s.MapTileUrlTemplate = MapTileUrlTemplate.Trim();
            s.MapAttribution = MapAttribution.Trim();
            s.MapTileCacheDirectory = tileDir.Length == 0 ? null : tileDir;
            s.HomeLatitude = lat;
            s.HomeLongitude = lon;
            s.FirstRunCompleted = FirstRunCompleted;
            s.Normalize();
        };
        return null;
    }

    private async Task SaveAsync()
    {
        if (Validate(out var apply) is { } err) { Dialogs.Warning(err); return; }
        string exe = DireWolfExePath.Trim().Trim('"'), conf = ConfigPath.Trim().Trim('"');
        if (exe.Length > 0 && !File.Exists(exe)) { Dialogs.Warning($"direwolf.exe was not found: {exe}"); return; }
        if (conf.Length > 0 && !File.Exists(conf)) { Dialogs.Warning($"The configuration file was not found: {conf}"); return; }
        bool configChanges = conf.Length > 0 && !string.Equals(conf, _shell.ConfigPath, StringComparison.OrdinalIgnoreCase);
        if (configChanges && !await ConfigurationSession.For(_shell).ConfirmLeaveAsync("switch to another configuration")) return;

        apply!(_shell.Settings);
        _shell.SaveSettings();
        if (exe.Length > 0 && !string.Equals(exe, _shell.DireWolfPath, StringComparison.OrdinalIgnoreCase)) _shell.SelectDireWolf(exe);
        if (configChanges) _shell.SelectConfig(conf);
        ThemeManager.Apply(_shell.Settings.Theme.ToString());
        Load(_shell.Settings);
        _shell.SetStatus(_shell.IsDireWolfRunning
            ? "Settings saved. Changes to Dire Wolf's program, configuration or options apply when Dire Wolf is restarted."
            : "Settings saved.");
    }

    private void Discard()
    {
        Load(_shell.Settings);
        ThemeManager.Apply(_shell.Settings.Theme.ToString());
    }

    private void Reset()
    {
        if (!Dialogs.Confirm("Reset the settings on this page to their defaults?\n\nKept: the direwolf.exe and configuration paths, the working folder, your external programs and the window layout. " +
                             "Messaging and map tiles are turned off. Nothing is saved until you press Save.", "Reset to defaults"))
            return;
        var d = new AppSettings
        {
            DireWolfExePath = DireWolfExePath, ConfigPath = ConfigPath, WorkingDirectory = WorkingDirectory, FirstRunCompleted = FirstRunCompleted,
        };
        Load(d);
        ThemeManager.Apply(d.Theme.ToString());
        IsModified = true;
    }

    private static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch (Exception) { /* reported by OpenFolder */ }
        var r = ExternalAppLauncher.OpenFolder(dir);
        if (!r.Success) Dialogs.Error("The folder could not be opened.", r.Error);
    }

    private static void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) { Dialogs.Error("The link could not be opened.", e.Message); }
    }

    // ------------------------------------------------------------------ about

    public string AppVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(SettingsViewModel).Assembly;
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (info != null && info.Contains('+')) info = info[..info.IndexOf('+')];
            return info ?? asm.GetName().Version?.ToString() ?? "unknown";
        }
    }

    public string DireWolfVersionText
    {
        get
        {
            string? v = _shell.Session.DireWolfVersion ?? _shell.LastCheck?.Summary?.Version;
            return v != null ? "Dire Wolf " + v : "Dire Wolf version: unknown until Dire Wolf has been started or the configuration checked.";
        }
    }

    public string LicenseText =>
        "Dire Wolf Station is free software under the GNU General Public License, version 2 or (at your option) any later version (GPL-2.0-or-later), " +
        "the same license as Dire Wolf. It comes with ABSOLUTELY NO WARRANTY.";

    public string CreditsText =>
        "Dire Wolf — the software modem, TNC and APRS engine this program controls — is written by John Langner, WB2OSZ, and contributors.";

    public string ThirdPartyText =>
        ".NET runtime and WPF: © .NET Foundation and contributors, MIT license. Map data (only when map tiles are enabled): © OpenStreetMap contributors, " +
        "available under the Open Database License (ODbL). See THIRD_PARTY_NOTICES.md.";

    public string PrivacyText =>
        "No telemetry, no advertising, no accounts. The program only connects to the network for what you enable: Dire Wolf's own ports on this computer, " +
        "map tiles (off by default), connection tests you start, and APRS-IS through Dire Wolf's IGate if you configure one.";
}
