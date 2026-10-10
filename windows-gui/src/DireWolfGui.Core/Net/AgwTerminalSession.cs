using System.Text;

namespace DireWolfGui.Core.Net;

public enum TerminalState { Disconnected, Connecting, Connected, Disconnecting }

/// <summary>
/// A connected-mode AX.25 terminal over AGW: register my call, connect (optionally via digipeaters),
/// send lines (terminated with CR), receive text. Server notices such as "*** CONNECTED With Station X"
/// are reported through <see cref="Notification"/>.
/// </summary>
public sealed class AgwTerminalSession : IDisposable
{
    private readonly AgwClient _client;
    private readonly object _lock = new();
    private TerminalState _state = TerminalState.Disconnected;

    public AgwTerminalSession(AgwClient client, int port, string myCall)
    {
        _client = client;
        AgwPort = port;
        MyCall = myCall.Trim().ToUpperInvariant();
        _client.FrameReceived += OnFrame;
        _client.Disconnected += OnClientDisconnected;
    }

    public int AgwPort { get; }
    public string MyCall { get; }
    public string? RemoteCall { get; private set; }
    public bool IsRegistered { get; private set; }
    public TerminalState State { get { lock (_lock) return _state; } }
    public Encoding TextEncoding { get; set; } = Encoding.UTF8;

    public event Action<TerminalState>? StateChanged;
    /// <summary>Text received from the remote station (CR line ends converted to "\n").</summary>
    public event Action<string>? DataReceived;
    /// <summary>Status lines such as "*** CONNECTED With Station N0ABC".</summary>
    public event Action<string>? Notification;

    public async Task<bool> RegisterAsync(CancellationToken ct = default)
    {
        IsRegistered = await _client.RegisterCallsignAsync(AgwPort, MyCall, null, ct).ConfigureAwait(false);
        return IsRegistered;
    }

    public async Task ConnectAsync(string remoteCall, IReadOnlyList<string>? via = null, CancellationToken ct = default)
    {
        remoteCall = remoteCall.Trim().ToUpperInvariant();
        lock (_lock)
        {
            if (_state != TerminalState.Disconnected) throw new InvalidOperationException("Already connected or connecting.");
            RemoteCall = remoteCall;
        }
        if (!IsRegistered) await RegisterAsync(ct).ConfigureAwait(false);
        SetState(TerminalState.Connecting);
        try
        {
            if (via is { Count: > 0 }) await _client.ConnectStationViaAsync(AgwPort, MyCall, remoteCall, via, ct).ConfigureAwait(false);
            else await _client.ConnectStationAsync(AgwPort, MyCall, remoteCall, ct).ConfigureAwait(false);
        }
        catch
        {
            SetState(TerminalState.Disconnected);
            throw;
        }
    }

    /// <summary>Sends one line; a carriage return is appended (packet BBS convention).</summary>
    public Task SendLineAsync(string text, CancellationToken ct = default)
    {
        if (State != TerminalState.Connected || RemoteCall == null) throw new InvalidOperationException("Not connected.");
        return _client.SendConnectedDataAsync(AgwPort, MyCall, RemoteCall, TextEncoding.GetBytes(text.TrimEnd('\r', '\n') + "\r"), 0xF0, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (RemoteCall == null || State == TerminalState.Disconnected) return;
        SetState(TerminalState.Disconnecting);
        await _client.DisconnectStationAsync(AgwPort, MyCall, RemoteCall, ct).ConfigureAwait(false);
    }

    private bool IsOurs(AgwFrame f) =>
        f.Port == AgwPort && string.Equals(f.CallTo, MyCall, StringComparison.OrdinalIgnoreCase)
        && (RemoteCall == null || string.Equals(f.CallFrom, RemoteCall, StringComparison.OrdinalIgnoreCase));

    private void OnFrame(AgwFrame f)
    {
        switch (f.DataKind)
        {
            case 'C' when string.Equals(f.CallTo, MyCall, StringComparison.OrdinalIgnoreCase) && f.Port == AgwPort:
                // Outgoing ("CONNECTED With") or incoming ("CONNECTED To") connection established.
                lock (_lock)
                {
                    if (_state == TerminalState.Connected && !string.Equals(f.CallFrom, RemoteCall, StringComparison.OrdinalIgnoreCase)) return;
                    RemoteCall = f.CallFrom;
                }
                Notification?.Invoke(f.DataText.TrimEnd('\r', '\n'));
                SetState(TerminalState.Connected);
                break;
            case 'D' when IsOurs(f):
                DataReceived?.Invoke(TextEncoding.GetString(f.Payload).Replace("\r\n", "\n").Replace('\r', '\n'));
                break;
            case 'd' when IsOurs(f):
                Notification?.Invoke(f.DataText.TrimEnd('\r', '\n'));
                SetState(TerminalState.Disconnected);
                break;
        }
    }

    private void OnClientDisconnected(string reason)
    {
        if (State == TerminalState.Disconnected) return;
        Notification?.Invoke("*** AGW connection lost: " + reason);
        SetState(TerminalState.Disconnected);
    }

    private void SetState(TerminalState s)
    {
        lock (_lock)
        {
            if (_state == s) return;
            _state = s;
        }
        StateChanged?.Invoke(s);
    }

    public void Dispose()
    {
        _client.FrameReceived -= OnFrame;
        _client.Disconnected -= OnClientDisconnected;
    }
}
