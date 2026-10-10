using System.Text;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Logging;
using DireWolfGui.Core.Net;
using DireWolfGui.Core.Packets;
using DireWolfGui.Core.Process;

namespace DireWolfGui.Core.Station;

public sealed class StationSessionOptions
{
    public required DireWolfLaunchOptions Launch { get; init; }
    /// <summary>Connect to Dire Wolf's AGW port (monitor + raw) to get raw bytes of received frames.</summary>
    public bool ConnectAgwMonitor { get; init; } = true;
    public string AgwHost { get; init; } = "127.0.0.1";
    /// <summary>AGW port; null = learn it from the "Ready to accept AGW client" line.</summary>
    public int? AgwPort { get; init; }
    /// <summary>Directory of Dire Wolf's daily CSV logs to follow; null = use <see cref="DireWolfLaunchOptions.LogDirectory"/> if set.</summary>
    public string? CsvLogDirectory { get; init; }
    /// <summary>Also load positions already in today's log file when starting.</summary>
    public bool CsvLoadExisting { get; init; }
    /// <summary>Our callsign (for messages addressed to us).</summary>
    public string? MyCall { get; init; }
    public IReadOnlyCollection<string> AdditionalCalls { get; init; } = Array.Empty<string>();
    /// <summary>Optional GUI-side copy of the console log (size-rotated, redacted).</summary>
    public string? SessionLogFile { get; init; }
}

/// <summary>
/// What the GUI talks to: runs direwolf, parses its output, keeps logs, packets, stations and messages.
/// Pull model: the UI polls GetSince(lastSequence) on <see cref="Log"/>, <see cref="Alerts"/> and <see cref="Packets"/>.
/// The session never transmits on its own; only <see cref="Messages"/>.SendAsync (user action) does.
/// </summary>
public sealed class StationSession : IAsyncDisposable
{
    private readonly TimeProvider _time;
    private readonly object _parseLock = new();
    private readonly object _rawLock = new();
    private readonly List<(DateTimeOffset Time, string Key, byte[] Bytes)> _pendingRaw = new();
    private readonly Dictionary<int, AudioLevelReading> _audioLevels = new();
    private readonly List<int> _kissPorts = new();
    private StationSessionOptions? _options;
    private DireWolfCsvLogTailer? _tailer;
    private RotatingLogFileWriter? _fileLog;
    private int _agwConnectStarted;
    private long _agwFrames;

    public StationSession(TimeProvider? timeProvider = null, int logCapacity = 20000, int packetCapacity = 5000)
    {
        _time = timeProvider ?? TimeProvider.System;
        Controller = new DireWolfProcessController(_time);
        Parser = new ConsoleOutputParser();
        Log = new SequencedBuffer<LogEntry>(logCapacity);
        Alerts = new SequencedBuffer<LogEntry>(500);
        Packets = new PacketStore(packetCapacity);
        Stations = new StationTracker();
        Agw = new AgwClient();
        Messages = new AprsMessageService(new AgwPacketSender(Agw), _time);

        Controller.OutputLine += (_, e) => OnOutputLine(e);
        Controller.Exited += (_, e) => OnExited(e);
        Controller.StateChanged += (_, e) => AddGuiLog(LogSeverity.Debug, $"Dire Wolf state: {e.NewState}");
        Parser.PacketParsed += OnPacket;
        Parser.Notice += OnNotice;
        Parser.AudioLevel += r => { if (r.Channel is int c) lock (_audioLevels) _audioLevels[c] = r; };
        Parser.AudioStatistics += s => LastAudioStatistics = s;
        Agw.FrameReceived += OnAgwFrame;
        Agw.Disconnected += reason => { AgwMonitorReady = false; AddGuiLog(LogSeverity.Info, "AGW monitor connection closed: " + reason, "agw"); };
    }

    public DireWolfProcessController Controller { get; }
    public ConsoleOutputParser Parser { get; }
    public SequencedBuffer<LogEntry> Log { get; }
    /// <summary>Recent warnings and errors (excluding packet decode chatter).</summary>
    public SequencedBuffer<LogEntry> Alerts { get; }
    public PacketStore Packets { get; }
    public StationTracker Stations { get; }
    public AprsMessageService Messages { get; }
    public AgwClient Agw { get; }

    public DireWolfState State => Controller.State;
    public TimeSpan? Uptime => Controller.Uptime;
    public string? DireWolfVersion => Parser.Version;
    public int? AgwPort { get; private set; }
    public bool AgwConnected => Agw.IsConnected;
    /// <summary>True once the AGW connection is confirmed and monitor/raw frames were requested.</summary>
    public bool AgwMonitorReady { get; private set; }
    public long AgwFramesReceived => Interlocked.Read(ref _agwFrames);
    public IReadOnlyList<int> KissPorts { get { lock (_kissPorts) return _kissPorts.ToArray(); } }
    public AudioStatisticsReading? LastAudioStatistics { get; private set; }
    public IReadOnlyDictionary<int, AudioLevelReading> LastAudioLevels { get { lock (_audioLevels) return new Dictionary<int, AudioLevelReading>(_audioLevels); } }
    public IReadOnlyList<ChannelActivity> ChannelActivity => Packets.GetChannelActivity();
    public ProcessResourceSample? SampleResources() => Controller.SampleResources();

    public async Task StartAsync(StationSessionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (State is DireWolfState.Starting or DireWolfState.Running or DireWolfState.Stopping)
            throw new InvalidOperationException("The station is already running.");
        _options = options;
        Interlocked.Exchange(ref _agwConnectStarted, 0);
        AgwMonitorReady = false;
        AgwPort = options.AgwPort;
        lock (_kissPorts) _kissPorts.Clear();
        Messages.MyCall = options.MyCall ?? "";
        Messages.AdditionalCalls = options.AdditionalCalls;
        _fileLog?.Dispose();
        _fileLog = options.SessionLogFile != null ? new RotatingLogFileWriter(options.SessionLogFile) : null;

        string? csvDir = options.CsvLogDirectory ?? options.Launch.LogDirectory;
        if (csvDir != null)
        {
            if (!Path.IsPathRooted(csvDir)) csvDir = Path.Combine(options.Launch.EffectiveWorkingDirectory, csvDir);
            _tailer = new DireWolfCsvLogTailer(csvDir, singleFile: false, startAtEnd: !options.CsvLoadExisting, _time);
            _tailer.RecordRead += r => Stations.Update(r, countAsPacket: false);
            _tailer.Start();
        }
        AddGuiLog(LogSeverity.Info, $"Starting {options.Launch.ExecutablePath} {string.Join(" ", DireWolfLaunchOptions.BuildArgumentList(options.Launch))}");
        try
        {
            await Controller.StartAsync(options.Launch, ct).ConfigureAwait(false);
        }
        catch (DireWolfStartException ex)
        {
            AddAlert(LogSeverity.Error, ex.Message, "gui");
            _tailer?.Dispose();
            _tailer = null;
            throw;
        }
        Messages.Start();
        if (options.ConnectAgwMonitor && options.AgwPort is int) StartAgwConnect();
    }

    public async Task<StopResult> StopAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await Agw.DisconnectAsync().ConfigureAwait(false);
        var r = await Controller.StopAsync(timeout, ct).ConfigureAwait(false);
        _tailer?.Poll();
        _tailer?.Dispose();
        _tailer = null;
        AddGuiLog(r.PttMayBeKeyed ? LogSeverity.Warning : LogSeverity.Info, r.Message);
        if (r.PttMayBeKeyed) AddAlert(LogSeverity.Warning, r.Message, "ptt");
        return r;
    }

    /// <summary>Adds a GUI-originated line to the log (and to alerts for warnings/errors).</summary>
    public void AddGuiLog(LogSeverity severity, string text, string category = "gui")
    {
        var e = new LogEntry(_time.GetUtcNow(), severity, SecretRedactor.Redact(text), category);
        Log.Add(e);
        _fileLog?.Write(e);
        if (severity is LogSeverity.Warning or LogSeverity.Error) Alerts.Add(new LogEntry(e.Time, severity, e.Text, category));
    }

    private void AddAlert(LogSeverity severity, string text, string category) =>
        Alerts.Add(new LogEntry(_time.GetUtcNow(), severity, SecretRedactor.Redact(text), category));

    private void OnOutputLine(DireWolfOutputLineEventArgs e)
    {
        string redacted = SecretRedactor.Redact(e.Line);
        ConsoleLineKind kind;
        lock (_parseLock) kind = Parser.ProcessLine(e.Line, e.Time);
        var (sev, cat) = LogClassifier.Classify(redacted);
        if (kind == ConsoleLineKind.Decoded) { sev = LogSeverity.Info; cat = "decode"; }
        else if (kind == ConsoleLineKind.Blank) return;
        if (e.IsStandardError && sev < LogSeverity.Warning) cat = "stderr";
        var entry = new LogEntry(e.Time, sev, redacted, cat);
        Log.Add(entry);
        _fileLog?.Write(entry);
        if (kind != ConsoleLineKind.Decoded && sev is LogSeverity.Warning or LogSeverity.Error)
            Alerts.Add(new LogEntry(e.Time, sev, redacted, cat));
    }

    private void OnExited(DireWolfExitedEventArgs e)
    {
        _ = Agw.DisconnectAsync();
        if (!e.Expected)
            AddAlert(LogSeverity.Error, "Dire Wolf stopped unexpectedly: " + (e.Diagnosis?.Summary ?? $"exit code {e.ExitCode}"), "direwolf");
        else
            AddGuiLog(LogSeverity.Info, $"Dire Wolf exited (code {e.ExitCode}).");
    }

    private void OnNotice(ConsoleNotice n)
    {
        if (n.Kind != ConsoleNoticeKind.ServerListening || n.Port is not int port) return;
        if (n.Protocol == "KISS") { lock (_kissPorts) if (!_kissPorts.Contains(port)) _kissPorts.Add(port); return; }
        if (n.Protocol == "AGW" && _options is { ConnectAgwMonitor: true })
        {
            AgwPort ??= port;
            StartAgwConnect();
        }
    }

    private void StartAgwConnect()
    {
        if (_options == null || AgwPort is not int port || Interlocked.Exchange(ref _agwConnectStarted, 1) == 1) return;
        string host = _options.AgwHost;
        _ = Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= 5 && Controller.State == DireWolfState.Running; attempt++)
            {
                try
                {
                    await Agw.ConnectAsync(host, port).ConfigureAwait(false);
                    // Dire Wolf's per-client reader thread polls once a second for a new socket, so commands
                    // are only processed after a delay. A version round trip makes sure it is listening.
                    var (major, minor) = await Agw.RequestVersionAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await Agw.EnableMonitoringAsync().ConfigureAwait(false);
                    await Agw.EnableRawFramesAsync().ConfigureAwait(false);
                    AgwMonitorReady = true;
                    AddGuiLog(LogSeverity.Info, $"Connected to Dire Wolf AGW port {host}:{port} (AGW version {major}.{minor}, monitor + raw frames).", "agw");
                    return;
                }
                catch (Exception ex)
                {
                    if (Agw.IsConnected) await Agw.DisconnectAsync().ConfigureAwait(false);
                    if (attempt == 5) AddGuiLog(LogSeverity.Warning, "Could not connect to the AGW port: " + ConnectionTester.Describe(ex, host, port), "agw");
                    else await Task.Delay(500 * attempt).ConfigureAwait(false);
                }
            }
        });
    }

    private void OnPacket(PacketRecord p)
    {
        // Check for an already received raw frame and store the packet under one lock, so a raw frame
        // arriving concurrently on the AGW thread either sees the stored packet or is seen here.
        lock (_rawLock)
        {
            if (p.Direction == PacketDirection.Received && p.RawFrame == null)
            {
                string key = Key(p.Source, p.Destination, p.Info);
                Prune();
                int i = _pendingRaw.FindIndex(r => r.Key == key);
                if (i >= 0) { p.RawFrame = _pendingRaw[i].Bytes; _pendingRaw.RemoveAt(i); }
            }
            Packets.Add(p);
        }
        Stations.Update(p);
        Messages.HandleIncoming(p);
    }

    private void OnAgwFrame(AgwFrame f)
    {
        Interlocked.Increment(ref _agwFrames);
        if (f.DataKind != 'K' || f.Payload.Length < 2) return;
        var bytes = f.Payload.AsSpan(1).ToArray();   // first byte is the KISS-style port/command byte
        if (!Ax25Frame.TryDecode(bytes, out var frame, out _)) return;
        string key = Key(frame!.Source.ToString(), frame.Destination.ToString(), DecodeInfo(frame.Info));
        var now = _time.GetUtcNow();
        lock (_rawLock)
        {
            // Console line may already be there (it usually is printed first).
            foreach (var p in Packets.GetLatest(50).Reverse())
            {
                if (now - p.Time > TimeSpan.FromSeconds(2)) break;
                if (p.Direction == PacketDirection.Received && p.RawFrame == null && Key(p.Source, p.Destination, p.Info) == key)
                {
                    p.RawFrame = bytes;
                    return;
                }
            }
            Prune();
            _pendingRaw.Add((now, key, bytes));
        }
    }

    private void Prune()
    {
        var limit = _time.GetUtcNow() - TimeSpan.FromSeconds(2);
        _pendingRaw.RemoveAll(r => r.Time < limit);
    }

    private static string Key(string src, string dst, string info) => $"{src.ToUpperInvariant()}>{dst.ToUpperInvariant()}:{info}";

    private static string DecodeInfo(byte[] info)
    {
        try { return new UTF8Encoding(false, true).GetString(info); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(info); }
    }

    public async ValueTask DisposeAsync()
    {
        if (State is DireWolfState.Running or DireWolfState.Starting) await StopAsync().ConfigureAwait(false);
        await Agw.DisposeAsync().ConfigureAwait(false);
        Messages.Dispose();
        _tailer?.Dispose();
        _fileLog?.Dispose();
    }
}
