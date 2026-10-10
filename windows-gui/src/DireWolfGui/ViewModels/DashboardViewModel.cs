using System.Collections.ObjectModel;
using DireWolfGui.Core.Config;
using DireWolfGui.Core.Logging;
using DireWolfGui.Core.Packets;
using DireWolfGui.Core.Process;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>One radio channel as the configuration defines it, with live activity.</summary>
public sealed record ChannelRow(string Channel, string Modem, string MyCall, string Ptt, string Activity, string LastHeard, string Level, string StateKind, bool IsWapr);

/// <summary>A short line in a dashboard list (packet, alert, service).</summary>
public sealed record DashboardItem(string Time, string Kind, string Text, string Detail = "");

/// <summary>
/// Station overview: process, channels, audio, client interfaces, services, recent packets
/// and alerts.  Everything shown comes from the running Dire Wolf or from the real
/// parser's view of the configuration; unknown values are labelled as such.
/// </summary>
public sealed class DashboardViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private int _ticks;
    private long _packetsVersion = -1, _alertsVersion = -1;

    public DashboardViewModel(MainViewModel main) : base("Dashboard", "", "Ctrl+1")
    {
        _main = main;
        CheckConfigCommand = new AsyncCommand(async () =>
        {
            var r = await _main.CheckConfigAsync();
            _main.SetStatus(r is null ? "Select direwolf.exe and a configuration first." :
                r.Status == CheckConfigStatus.Ok ? "Dire Wolf accepted the configuration without warnings." :
                r.Status == CheckConfigStatus.Unsupported ? r.Message ?? ConfigChecker.UnsupportedMessage :
                $"Dire Wolf reported {r.Diagnostics.Count} message(s) for the configuration; see Configuration › Diagnostics.");
            Refresh();
        });
        GoCommand = new RelayCommand(p =>
        {
            var page = _main.Pages.FirstOrDefault(x => x.GetType().Name == p as string);
            if (page is not null) _main.SelectedPage = page;
        });
    }

    public MainViewModel Main => _main;
    public AsyncCommand CheckConfigCommand { get; }
    public RelayCommand GoCommand { get; }

    public ObservableCollection<ChannelRow> Channels { get; } = [];
    public ObservableCollection<DashboardItem> RecentPackets { get; } = [];
    public ObservableCollection<DashboardItem> Alerts { get; } = [];
    public ObservableCollection<DashboardItem> Services { get; } = [];

    public string ProcessSummary { get; private set; } = "";
    public string ProcessDetail { get; private set; } = "";
    public string AudioSummary { get; private set; } = "";
    public string PortsSummary { get; private set; } = "";
    public string PortsWarning { get; private set; } = "";
    public string ConfigSummary { get; private set; } = "";
    public string ServicesSummary { get; private set; } = "";
    public bool NeedsSetup => string.IsNullOrEmpty(_main.DireWolfPath) || !File.Exists(_main.DireWolfPath)
                              || string.IsNullOrEmpty(_main.ConfigPath) || !File.Exists(_main.ConfigPath);

    protected override void OnShown()
    {
        _packetsVersion = _alertsVersion = -1;
        Refresh();
        if (_main.LastCheck is null && !NeedsSetup) _ = _main.CheckConfigAsync().ContinueWith(_ => { }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public override void Tick()
    {
        if (++_ticks % 4 == 0) Refresh();
    }

    private void Refresh()
    {
        var s = _main.Session;
        var running = _main.IsDireWolfRunning;
        var check = _main.LastCheck;
        var summary = check?.Summary;

        // Process
        ProcessSummary = running
            ? $"Running {Converters.AgeConverter.Format(s.Uptime ?? TimeSpan.Zero)} · process {s.Controller.ProcessId}"
            : _main.LastExitSummary ?? "Not running";
        ProcessDetail = string.Join("\n", new[]
        {
            "Version: " + (s.DireWolfVersion ?? summary?.Version ?? "unknown until Dire Wolf runs or the configuration is checked"),
            "Program: " + (_main.DireWolfPath ?? "not selected"),
            "Configuration: " + (_main.ActiveConfig ?? _main.ConfigPath ?? "not selected"),
        });

        // Configuration check
        ConfigSummary = check is null
            ? (NeedsSetup ? "Choose direwolf.exe and a configuration (Setup wizard)." : "Not checked yet.")
            : check.Status switch
            {
                CheckConfigStatus.Ok => "Dire Wolf's own parser accepted the configuration.",
                CheckConfigStatus.Diagnostics => $"Dire Wolf reported {check.Diagnostics.Count} message(s) — open Configuration › Diagnostics.",
                CheckConfigStatus.Unsupported => "This Dire Wolf build cannot check configurations (no --check-config); only the GUI's own checks are available.",
                _ => check.Message ?? "The check could not run.",
            };

        // Channels: what the real parser says the configuration defines, plus live activity.
        var activity = s.ChannelActivity.ToDictionary(a => a.Channel);
        var levels = s.LastAudioLevels;
        Channels.Clear();
        foreach (var c in summary?.Channels ?? [])
        {
            activity.TryGetValue(c.Number, out var a);
            var heard = a?.LastReceived;
            var level = a?.LastAudioLevel ?? (levels.TryGetValue(c.Number, out var l) ? l.Level : null);
            var modem = c.Medium switch
            {
                Core.Config.ChannelMedium.IGate => "IGate (virtual)",
                Core.Config.ChannelMedium.NetTnc => "Network TNC",
                _ => c.IsWapr ? $"WAPR {c.WaprProfile} (experimental)" + (c.WaprAirtimePercent > 0 ? $", airtime ≤ {c.WaprAirtimePercent:0} %" : "")
                    : $"{c.Baud} bd {c.Modem}" + (c.Fx25Tx is not null ? " · FX.25 TX" : "") + (c.Il2pTx is not null ? " · IL2P TX" : ""),
            };
            var state = !running ? "Neutral" : heard is null ? "Info" : DateTimeOffset.Now - heard < TimeSpan.FromMinutes(10) ? "Success" : "Stale";
            Channels.Add(new ChannelRow(
                c.Number.ToString(), modem, string.IsNullOrWhiteSpace(c.MyCall) ? "(none)" : c.MyCall,
                c.Ptt is null or "NONE" ? "none / VOX" : c.Ptt,
                running ? $"RX {a?.Received ?? 0} · TX {a?.Transmitted ?? 0}" : "not running",
                heard is null ? (running ? "nothing heard yet" : "—") : Converters.AgeConverter.Format(DateTimeOffset.Now - heard.Value) + " ago",
                level is null ? "—" : $"{level}" + (level > 110 ? " (too high)" : level < 5 ? " (too low)" : ""),
                state, c.IsWapr));
        }

        // Audio
        var st = s.LastAudioStatistics;
        var devices = summary?.AudioDevices.Select(d => $"Device {d.Index}: in “{d.Input}”, out “{d.Output}”, {d.SampleRate} Hz, {(d.Channels == 2 ? "stereo" : "mono")}") ?? [];
        AudioSummary = string.Join("\n", devices.DefaultIfEmpty("Audio devices are shown after the configuration has been checked."));
        AudioSummary += st is not null
            ? $"\nLive: device {st.Device} at about {st.SampleRateKHz:0.0} kHz, {st.Errors} error(s), levels {string.Join(", ", st.ChannelLevels.Select(kv => $"CH{kv.Key} {kv.Value}"))} ({Converters.AgeConverter.Format(DateTimeOffset.Now - st.Time)} ago)"
            : running ? (_main.Settings.AudioStatisticsInterval > 0 ? "\nWaiting for Dire Wolf's first audio statistics…" : "\nPeriodic audio statistics are off (Settings › Audio statistics interval). Levels of received packets are shown per channel.")
            : "";

        // Client interfaces
        var agw = s.AgwPort ?? summary?.AgwPort;
        var kiss = (running ? s.KissPorts : summary?.KissPorts.Select(k => k.Port).ToList()) ?? [];
        PortsSummary = (agw is > 0 ? $"AGWPE: port {agw}" + (running ? (s.AgwMonitorReady ? " · this application is connected" : " · this application is not connected") : "") : "AGWPE: off")
                       + "\n" + (kiss.Count > 0 ? "KISS TCP: port " + string.Join(", ", kiss) : "KISS TCP: off")
                       + (summary?.SerialKiss is { } sk ? $"\nSerial KISS: {sk.Device} at {sk.Speed} bps" : "");
        PortsWarning = summary is null ? "" : summary.BindLocalOnly
            ? "Only programs on this computer can connect (TCPBIND LOCAL)."
            : "Other computers on your network can connect to these ports and transmit through your radio. Use TCPBIND LOCAL or a firewall rule if that is not intended.";

        // Services that transmit or forward
        Services.Clear();
        var report = _main.Services;
        foreach (var sv in report?.Services ?? [])
            Services.Add(new DashboardItem(sv.Channel is int ch ? $"ch {ch}" : "", sv.Transmits ? "Warning" : "Info", sv.Title, sv.Explanation));
        ServicesSummary = report is null ? "Select a configuration to see its services."
            : report.Services.Count == 0 ? "Nothing in the configuration transmits or forwards on its own."
            : $"{report.Services.Count(x => x.Transmits)} can transmit · {report.Services.Count(x => x.Forwards)} forward traffic";

        // Recent packets and alerts (only rebuilt when something new arrived)
        if (s.Packets.LastSequence != _packetsVersion)
        {
            _packetsVersion = s.Packets.LastSequence;
            RecentPackets.Clear();
            foreach (var p in s.Packets.GetLatest(12).Reverse())
                RecentPackets.Add(new DashboardItem(p.Time.ToLocalTime().ToString("HH:mm:ss"), OriginKind(p), $"[{p.Label}] {p.Source}>{p.Destination}: {Shorten(p.Info)}", OriginText(p)));
        }
        if (s.Alerts.LastSequence != _alertsVersion)
        {
            _alertsVersion = s.Alerts.LastSequence;
            Alerts.Clear();
            foreach (var a in s.Alerts.GetLatest(10).Reverse())
                Alerts.Add(new DashboardItem(a.Time.ToLocalTime().ToString("HH:mm:ss"), a.Severity == LogSeverity.Error ? "Error" : "Warning", a.Text));
        }

        foreach (var name in new[] { nameof(ProcessSummary), nameof(ProcessDetail), nameof(AudioSummary), nameof(PortsSummary),
                     nameof(PortsWarning), nameof(ConfigSummary), nameof(ServicesSummary), nameof(NeedsSetup) })
            OnPropertyChanged(name);
    }

    private static string Shorten(string s) => s.Length > 90 ? s[..90] + "…" : s;

    internal static string OriginKind(PacketRecord p) => p.Origin switch
    {
        PacketOrigin.Radio => "Rx",
        PacketOrigin.Local => "Tx",
        PacketOrigin.FromAprsIs or PacketOrigin.ToAprsIs or PacketOrigin.IgateToRadio => "IgateColor",
        PacketOrigin.Gateway => "Gateway",
        _ => p.Direction == PacketDirection.Transmitted ? "Tx" : "Neutral",
    };

    internal static string OriginText(PacketRecord p) => p.Origin switch
    {
        PacketOrigin.Radio => p.IsWapr ? "Received on radio (WAPR)" : "Received on radio",
        PacketOrigin.Local => "Transmitted by this station",
        PacketOrigin.FromAprsIs => "Received from APRS-IS",
        PacketOrigin.ToAprsIs => "Sent to APRS-IS",
        PacketOrigin.IgateToRadio => "IGate: from APRS-IS to radio",
        PacketOrigin.Gateway => "Relayed by a WAPR gateway",
        PacketOrigin.NetworkTnc => "Network TNC",
        PacketOrigin.Dtmf => "DTMF (APRStt)",
        PacketOrigin.Ais => "AIS",
        _ => p.Direction == PacketDirection.Transmitted ? "Transmitted" : "Received",
    };
}
