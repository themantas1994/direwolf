using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using DireWolfGui.Core.Net;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>A radio port reported by Dire Wolf's AGW interface ('G').</summary>
public sealed record AgwPortChoice(int Port, string Description)
{
    public string Label => $"Port {Port} — {Description}";
    public override string ToString() => Label;
}

/// <summary>
/// "Packet terminal": connected-mode AX.25 through Dire Wolf's AGW interface, on its own AGW connection
/// (separate from the station's monitor connection). Nothing is transmitted until the user presses Connect / Send.
/// </summary>
public sealed class TerminalViewModel : PageViewModel, IDisposable
{
    private const int MaxLines = 5000;
    private readonly IShell _shell;
    private readonly ConcurrentQueue<Action> _fromBackground = new();
    private readonly List<string> _history = new();
    private int _historyIndex = -1;
    private AgwClient? _client;
    private AgwTerminalSession? _terminal;
    private CancellationTokenSource? _cts = new();
    private DateTimeOffset _lastOutstandingQuery;
    private int _outstandingBusy;
    private bool _disposed;

    public TerminalViewModel(IShell shell) : base("Packet terminal", "", "Ctrl+5")
    {
        _shell = shell;
        ConnectTncCommand = new AsyncCommand(ConnectTncAsync, () => !IsTncConnected && !_tncBusy);
        DisconnectTncCommand = new AsyncCommand(DisconnectTncAsync, () => IsTncConnected);
        ConnectCommand = new AsyncCommand(ConnectStationAsync, () => IsTncConnected && State == TerminalState.Disconnected);
        DisconnectCommand = new AsyncCommand(DisconnectStationAsync, () => IsTncConnected && State is TerminalState.Connected or TerminalState.Connecting);
        SendCommand = new AsyncCommand(SendAsync, () => State == TerminalState.Connected && Input.Length > 0);
        SaveTranscriptCommand = new AsyncCommand(SaveTranscriptAsync, () => Lines.Count > 0);
        ClearCommand = new RelayCommand(() => Lines.ReplaceAll([]));
        RefreshPortsCommand = new AsyncCommand(RefreshPortsAsync, () => IsTncConnected);
        if (Application.Current is { } app) app.Exit += (_, _) => Dispose();
        AddSystem("Connected mode uses Dire Wolf's AGW interface. Press \"Connect to TNC\", then enter the station to call and press Connect.");
    }

    public BatchObservableCollection<TerminalLine> Lines { get; } = new() { ResetThreshold = 500 };
    public ObservableCollection<AgwPortChoice> Ports { get; } = new();

    /// <summary>The view scrolls to the newest line.</summary>
    public event EventHandler? ScrollToEndRequested;

    public ICommand ConnectTncCommand { get; }
    public ICommand DisconnectTncCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand SaveTranscriptCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RefreshPortsCommand { get; }

    // ---- TNC (AGW) connection ----

    public string AgwHost => _shell.Settings.AgwHost;
    public int AgwPortNumber => _shell.Settings.AgwPortOverride ?? _shell.Session.AgwPort ?? 8000;
    public string TncEndpointText => $"{AgwHost}:{AgwPortNumber}";

    private bool _tncBusy;
    private bool _isTncConnected;
    public bool IsTncConnected
    {
        get => _isTncConnected;
        private set { if (Set(ref _isTncConnected, value)) UpdateStateText(); }
    }

    private string _versionText = "";
    public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }

    private string _capabilitiesText = "";
    public string CapabilitiesText { get => _capabilitiesText; private set => Set(ref _capabilitiesText, value); }

    private AgwPortChoice? _selectedPort;
    public AgwPortChoice? SelectedPort
    {
        get => _selectedPort;
        set { if (Set(ref _selectedPort, value)) _ = LoadCapabilitiesAsync(); }
    }

    private async Task ConnectTncAsync()
    {
        if (IsTncConnected) return;
        _tncBusy = true;
        string host = AgwHost;
        int port = AgwPortNumber;
        OnPropertyChanged(nameof(TncEndpointText));
        AddSystem($"Connecting to Dire Wolf's AGW interface at {host}:{port}…");
        var client = new AgwClient();
        try
        {
            await client.ConnectAsync(host, port, TimeSpan.FromSeconds(5));
            client.Disconnected += reason => _fromBackground.Enqueue(() => OnTncLost(client, reason));
            // Dire Wolf starts reading a new client's commands up to ~1 s after accepting it; 'R' confirms it listens.
            var (major, minor) = await client.RequestVersionAsync(TimeSpan.FromSeconds(5));
            _client = client;
            IsTncConnected = true;
            VersionText = $"AGW protocol version {major}.{minor} reported by {host}:{port}";
            AddSystem($"Connected to the TNC ({VersionText}).");
            await RefreshPortsAsync();
        }
        catch (Exception ex)
        {
            await client.DisposeAsync();
            if (ReferenceEquals(_client, client)) _client = null;
            IsTncConnected = false;
            AddError("Could not connect to the TNC: " + ConnectionTester.Describe(ex, host, port)
                     + (_shell.Session.State != Core.Process.DireWolfState.Running ? " Dire Wolf is not running — press Start (F5)." : ""));
        }
        finally
        {
            _tncBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OnTncLost(AgwClient client, string reason)
    {
        if (!ReferenceEquals(client, _client)) return;
        _terminal?.Dispose();
        _terminal = null;
        _client = null;
        IsTncConnected = false;
        State = TerminalState.Disconnected;
        Outstanding = null;
        AddError("The connection to the TNC ended: " + reason);
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task DisconnectTncAsync()
    {
        var client = _client;
        if (client == null) return;
        if (_terminal is { State: TerminalState.Connected or TerminalState.Connecting } t)
        {
            try { await t.DisconnectAsync(); } catch (Exception ex) { AddError("Disconnect request failed: " + ex.Message); }
        }
        _terminal?.Dispose();
        _terminal = null;
        _client = null;
        await client.DisposeAsync();
        IsTncConnected = false;
        State = TerminalState.Disconnected;
        Outstanding = null;
        AddSystem("Disconnected from the TNC.");
    }

    private async Task RefreshPortsAsync()
    {
        if (_client is not { } client) return;
        try
        {
            var info = await client.RequestPortInfoAsync(TimeSpan.FromSeconds(5));
            var keep = SelectedPort?.Port ?? 0;
            Ports.Clear();
            for (int i = 0; i < info.Descriptions.Count; i++) Ports.Add(new AgwPortChoice(i, info.Descriptions[i]));
            if (Ports.Count == 0) AddError("Dire Wolf reported no radio ports.");
            SelectedPort = Ports.FirstOrDefault(p => p.Port == keep) ?? Ports.FirstOrDefault();
        }
        catch (Exception ex)
        {
            AddError("Could not read the radio ports ('G'): " + ex.Message);
            if (Ports.Count == 0) { Ports.Add(new AgwPortChoice(0, "radio channel 0 (port list not available)")); SelectedPort = Ports[0]; }
        }
    }

    private async Task LoadCapabilitiesAsync()
    {
        if (_client is not { } client || SelectedPort is not { } p) { CapabilitiesText = ""; return; }
        try
        {
            var g = await client.RequestPortCapabilitiesAsync(p.Port, TimeSpan.FromSeconds(3));
            CapabilitiesText = DescribeCapabilities(g);
        }
        catch (Exception ex)
        {
            CapabilitiesText = "Capabilities ('g') not available: " + ex.Message;
        }
    }

    /// <summary>The AGW 'g' reply fields, as reported (not interpreted further).</summary>
    public static string DescribeCapabilities(byte[] g)
    {
        if (g.Length < 8) return $"Capabilities reply too short ({g.Length} bytes).";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Baud rate code {g[0]}, traffic level {(g[1] == 0xFF ? "n/a" : g[1].ToString(CultureInfo.InvariantCulture))}\n");
        sb.Append(CultureInfo.InvariantCulture, $"TX delay {g[2]}, TX tail {g[3]}, persist {g[4]}, slot time {g[5]}, max frame {g[6]}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Active connections {g[7]}");
        if (g.Length >= 12) sb.Append(CultureInfo.InvariantCulture, $", bytes received in last 2 min {BinaryPrimitives.ReadInt32LittleEndian(g.AsSpan(8))}");
        return sb.ToString();
    }

    // ---- AX.25 connection ----

    private string _myCall = "";
    public string MyCall { get => _myCall; set => Set(ref _myCall, (value ?? "").Trim().ToUpperInvariant()); }

    private string _remoteCall = "";
    public string RemoteCall { get => _remoteCall; set => Set(ref _remoteCall, (value ?? "").Trim().ToUpperInvariant()); }

    private string _via = "";
    public string Via { get => _via; set => Set(ref _via, value ?? ""); }

    private TerminalState _state = TerminalState.Disconnected;
    public TerminalState State
    {
        get => _state;
        private set { if (Set(ref _state, value)) { UpdateStateText(); CommandManager.InvalidateRequerySuggested(); } }
    }

    private string _stateText = "";
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    private string _stateKind = "Neutral";
    public string StateKind { get => _stateKind; private set => Set(ref _stateKind, value); }

    private int? _outstanding;
    /// <summary>Frames Dire Wolf still has queued for this connection ('Y'), polled while connected.</summary>
    public int? Outstanding
    {
        get => _outstanding;
        private set { if (Set(ref _outstanding, value)) OnPropertyChanged(nameof(OutstandingText)); }
    }
    public string OutstandingText => Outstanding is int n ? $"{n} frame(s) waiting to be sent or acknowledged" : "";

    private void UpdateStateText()
    {
        (StateText, StateKind) = (IsTncConnected, State) switch
        {
            (false, _) => ("TNC not connected", "Neutral"),
            (true, TerminalState.Disconnected) => ("TNC connected · no station", "Accent"),
            (true, TerminalState.Connecting) => ($"Connecting to {RemoteCall}…", "Warning"),
            (true, TerminalState.Connected) => ($"Connected to {_terminal?.RemoteCall ?? RemoteCall}", "Success"),
            (true, TerminalState.Disconnecting) => ("Disconnecting…", "Warning"),
            _ => (State.ToString(), "Neutral"),
        };
    }

    private async Task ConnectStationAsync()
    {
        if (_client is not { } client || !IsTncConnected) return;
        if (string.IsNullOrWhiteSpace(MyCall) || !Core.Packets.Ax25Address.TryParse(MyCall, out _))
        { AddError("Enter your callsign (with optional -SSID) in \"My call\"."); return; }
        if (string.IsNullOrWhiteSpace(RemoteCall) || !Core.Packets.Ax25Address.TryParse(RemoteCall, out _))
        { AddError("Enter the callsign of the station to connect to."); return; }
        var via = TerminalText.ParseVia(Via, out var viaError);
        if (via == null) { AddError("Via: " + viaError); return; }
        int port = SelectedPort?.Port ?? 0;

        if (_terminal == null || _terminal.MyCall != MyCall || _terminal.AgwPort != port)
        {
            _terminal?.Dispose();
            var t = new AgwTerminalSession(client, port, MyCall);
            t.StateChanged += s => _fromBackground.Enqueue(() => { if (ReferenceEquals(t, _terminal)) State = s; });
            t.DataReceived += text => _fromBackground.Enqueue(() => { if (ReferenceEquals(t, _terminal)) AddLines(TerminalLineKind.Received, text); });
            t.Notification += text => _fromBackground.Enqueue(() => { if (ReferenceEquals(t, _terminal)) AddSystem(text); });
            _terminal = t;
            try
            {
                if (!await t.RegisterAsync())
                {
                    AddError($"Dire Wolf did not accept the callsign {MyCall} ('X' registration refused — is it already registered by another application?).");
                    return;
                }
            }
            catch (Exception ex)
            {
                AddError("Callsign registration failed: " + ex.Message);
                return;
            }
        }
        AddSystem($"Calling {RemoteCall} from {MyCall} on port {port}{(via.Count > 0 ? " via " + string.Join(",", via) : "")} (this transmits)…");
        try
        {
            await _terminal.ConnectAsync(RemoteCall, via);
            _lastOutstandingQuery = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            AddError("Connect failed: " + ex.Message);
        }
        State = _terminal?.State ?? TerminalState.Disconnected;
    }

    private async Task DisconnectStationAsync()
    {
        if (_terminal is not { } t) return;
        try
        {
            AddSystem("Disconnecting…");
            await t.DisconnectAsync();
        }
        catch (Exception ex)
        {
            AddError("Disconnect failed: " + ex.Message);
        }
        State = t.State;
    }

    // ---- Input ----

    private string _input = "";
    public string Input { get => _input; set => Set(ref _input, value ?? ""); }

    private async Task SendAsync()
    {
        if (_terminal is not { State: TerminalState.Connected } t) return;
        var text = Input;
        if (text.Length == 0) return;
        if (_history.Count == 0 || _history[^1] != text) _history.Add(text);
        if (_history.Count > 100) _history.RemoveAt(0);
        _historyIndex = -1;
        Input = "";
        try
        {
            await t.SendLineAsync(text);
            AddLines(TerminalLineKind.Sent, text);
        }
        catch (Exception ex)
        {
            AddError("Not sent: " + ex.Message);
            Input = text;
        }
    }

    /// <summary>Up arrow: previous line from the history.</summary>
    public void HistoryPrevious()
    {
        if (_history.Count == 0) return;
        _historyIndex = _historyIndex < 0 ? _history.Count - 1 : Math.Max(0, _historyIndex - 1);
        Input = _history[_historyIndex];
    }

    /// <summary>Down arrow: next line from the history, or empty after the newest.</summary>
    public void HistoryNext()
    {
        if (_historyIndex < 0) return;
        _historyIndex++;
        if (_historyIndex >= _history.Count) { _historyIndex = -1; Input = ""; }
        else Input = _history[_historyIndex];
    }

    // ---- Output ----

    private bool _autoScroll = true;
    public bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value); }

    private void AddSystem(string text) => AddLines(TerminalLineKind.System, text);
    private void AddError(string text) => AddLines(TerminalLineKind.Error, text);

    private void AddLines(TerminalLineKind kind, string text)
    {
        TerminalText.Append(Lines, kind, text, DateTimeOffset.Now);
        if (Lines.Count > MaxLines + 500) Lines.RemoveFirst(Lines.Count - MaxLines);
        if (AutoScroll) ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveTranscriptAsync()
    {
        var path = Dialogs.SaveFile("Text file (*.txt)|*.txt", $"terminal-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        if (path == null) return;
        var text = TerminalText.Transcript(Lines.ToList());
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        _shell.SetStatus("Transcript saved to " + path);
    }

    // ---- Polling ----

    protected override void OnShown()
    {
        if (string.IsNullOrEmpty(MyCall) && !string.IsNullOrWhiteSpace(_shell.Session.Messages.MyCall))
            MyCall = _shell.Session.Messages.MyCall;
        OnPropertyChanged(nameof(TncEndpointText));
        UpdateStateText();
    }

    public override void Tick() => Drain();

    /// <summary>Keeps the transcript current (and memory bounded) while the page is hidden.</summary>
    public override void BackgroundTick() => Drain();

    private void Drain()
    {
        int n = 0;
        while (n++ < 500 && _fromBackground.TryDequeue(out var a))
        {
            try { a(); } catch (Exception ex) { AddError(ex.Message); }
        }
        if (State == TerminalState.Connected && _client is { } c && _terminal is { RemoteCall: { } remote } t
            && DateTimeOffset.Now - _lastOutstandingQuery > TimeSpan.FromSeconds(3)
            && Interlocked.Exchange(ref _outstandingBusy, 1) == 0)
        {
            _lastOutstandingQuery = DateTimeOffset.Now;
            _ = QueryOutstandingAsync(c, t.AgwPort, t.MyCall, remote);
        }
        else if (State != TerminalState.Connected && Outstanding != null) Outstanding = null;
    }

    private async Task QueryOutstandingAsync(AgwClient c, int port, string myCall, string remote)
    {
        try
        {
            int n = await c.RequestOutstandingForConnectionAsync(port, myCall, remote, TimeSpan.FromSeconds(3), _cts?.Token ?? default);
            Outstanding = n;
        }
        catch
        {
            Outstanding = null;   // not answered (e.g. older Dire Wolf); not an error worth showing
        }
        finally
        {
            Interlocked.Exchange(ref _outstandingBusy, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _terminal?.Dispose();
        _terminal = null;
        var c = _client;
        _client = null;
        if (c != null) _ = c.DisposeAsync().AsTask();
    }
}
