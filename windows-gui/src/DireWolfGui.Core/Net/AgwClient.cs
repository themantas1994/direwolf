using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DireWolfGui.Core.Net;

/// <summary>
/// Client for the AGW PE TCP protocol as implemented by Dire Wolf (src/server.c).
/// A background task reads frames and raises <see cref="FrameReceived"/> on that task's thread.
/// Note: Dire Wolf treats 'm' and 'k' as toggles, so call <see cref="EnableMonitoringAsync"/> / <see cref="EnableRawFramesAsync"/> once per connection.
/// Dire Wolf only starts reading a new client's commands up to ~1 s after accepting it; do a request with a reply
/// (e.g. <see cref="RequestVersionAsync"/>) first when frames must not be missed right after connecting.
/// </summary>
public sealed class AgwClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _lock = new();
    private readonly List<(Func<AgwFrame, bool> Match, TaskCompletionSource<AgwFrame> Tcs)> _waiters = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _reader;
    private int _disconnectRaised;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public bool IsConnected => _tcp?.Connected == true && _stream != null;
    public string? Host { get; private set; }
    public int Port { get; private set; }
    public bool MonitoringEnabled { get; private set; }
    public bool RawFramesEnabled { get; private set; }

    public event Action<AgwFrame>? FrameReceived;
    /// <summary>Raised once when the connection ends (server closed, network error, protocol error or <see cref="DisconnectAsync"/>).</summary>
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(string host, int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (IsConnected) throw new InvalidOperationException("Already connected.");
        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"Timed out connecting to {host}:{port}.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        Host = host;
        Port = port;
        _tcp = tcp;
        _stream = tcp.GetStream();
        MonitoringEnabled = RawFramesEnabled = false;
        Interlocked.Exchange(ref _disconnectRaised, 0);
        _readCts = new CancellationTokenSource();
        var stream = _stream;
        var token = _readCts.Token;
        _reader = Task.Run(() => ReadLoopAsync(stream, token));
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        string reason = "Connection closed by the server.";
        var header = new byte[AgwFrame.HeaderLength];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                var frame = AgwFrame.DecodeHeader(header, out uint len);
                if (len > AgwFrame.MaxDataLength)
                {
                    reason = $"Protocol error: data length {len} is too large.";
                    break;
                }
                var data = new byte[len];
                if (len > 0) await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
                frame = frame with { Data = data };
                Dispatch(frame);
            }
            if (ct.IsCancellationRequested) reason = "Disconnected.";
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) { reason = "Disconnected."; }
        catch (IOException ex) { reason = "Connection lost: " + (ex.InnerException?.Message ?? ex.Message); }
        catch (ObjectDisposedException) { reason = "Disconnected."; }
        Close(reason, stream);
    }

    private void Dispatch(AgwFrame frame)
    {
        List<TaskCompletionSource<AgwFrame>>? done = null;
        lock (_lock)
        {
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Match(frame))
                {
                    (done ??= new()).Add(_waiters[i].Tcs);
                    _waiters.RemoveAt(i);
                }
            }
        }
        if (done != null) foreach (var t in done) t.TrySetResult(frame);
        try { FrameReceived?.Invoke(frame); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"AGW FrameReceived handler failed: {ex}"); }
    }

    private void Close(string reason, NetworkStream? owner)
    {
        List<TaskCompletionSource<AgwFrame>> pending;
        lock (_lock)
        {
            if (owner != null && !ReferenceEquals(owner, _stream)) return;   // an older connection's reader
            _stream?.Dispose();
            _tcp?.Dispose();
            _stream = null;
            _tcp = null;
            pending = _waiters.Select(w => w.Tcs).ToList();
            _waiters.Clear();
        }
        foreach (var t in pending) t.TrySetException(new IOException("AGW connection closed: " + reason));
        if (Interlocked.Exchange(ref _disconnectRaised, 1) == 0)
        {
            try { Disconnected?.Invoke(reason); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
        }
    }

    public async Task DisconnectAsync()
    {
        var stream = _stream;
        if (stream == null) return;
        _readCts?.Cancel();
        stream.Dispose();
        if (_reader != null) { try { await _reader.ConfigureAwait(false); } catch { /* reader reports via Disconnected */ } }
        Close("Disconnected.", stream);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    public async Task SendAsync(AgwFrame frame, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected to the AGW server.");
        var bytes = frame.Encode();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new IOException("Could not send to the AGW server: " + ex.Message, ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Sends <paramref name="request"/> and waits for the first frame matching <paramref name="reply"/>.</summary>
    public async Task<AgwFrame> RequestAsync(AgwFrame request, Func<AgwFrame, bool> reply, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<AgwFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = (reply, tcs);
        lock (_lock) _waiters.Add(entry);
        try
        {
            await SendAsync(request, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No '{request.DataKind}' reply from the AGW server.");
        }
        finally
        {
            lock (_lock) _waiters.Remove(entry);
        }
    }

    // ---- Requests with replies ----

    /// <summary>'R': server version (Dire Wolf answers 2005.127).</summary>
    public async Task<(int Major, int Minor)> RequestVersionAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var f = await RequestAsync(new AgwFrame(0, 'R'), r => r.DataKind == 'R', timeout, ct).ConfigureAwait(false);
        var d = f.Payload;
        if (d.Length < 8) throw new InvalidDataException("Short 'R' reply.");
        return (BinaryPrimitives.ReadInt32LittleEndian(d), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4)));
    }

    /// <summary>'G': radio ports.</summary>
    public async Task<AgwPortInfo> RequestPortInfoAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var f = await RequestAsync(new AgwFrame(0, 'G'), r => r.DataKind == 'G', timeout, ct).ConfigureAwait(false);
        return AgwPortInfo.Parse(f.DataText);
    }

    /// <summary>'g': capabilities of a port (raw 12 byte reply: baud code, traffic, TX delay, TX tail, persist, slottime, maxframe, ...).</summary>
    public async Task<byte[]> RequestPortCapabilitiesAsync(int port, TimeSpan? timeout = null, CancellationToken ct = default) =>
        (await RequestAsync(new AgwFrame(port, 'g'), r => r.DataKind == 'g' && r.Port == port, timeout, ct).ConfigureAwait(false)).Payload;

    /// <summary>'X': register a callsign for connected mode. True if the server accepted it.</summary>
    public async Task<bool> RegisterCallsignAsync(int port, string callsign, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var f = await RequestAsync(new AgwFrame(port, 'X', callsign), r => r.DataKind == 'X' && string.Equals(r.CallFrom, callsign, StringComparison.OrdinalIgnoreCase), timeout, ct).ConfigureAwait(false);
        return f.Payload.Length > 0 && f.Payload[0] == 1;
    }

    /// <summary>'y': frames waiting to be transmitted on a port.</summary>
    public async Task<int> RequestOutstandingFramesAsync(int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var f = await RequestAsync(new AgwFrame(port, 'y'), r => r.DataKind == 'y' && r.Port == port, timeout, ct).ConfigureAwait(false);
        return f.Payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(f.Payload) : 0;
    }

    /// <summary>'Y': frames waiting for a particular connection.</summary>
    public async Task<int> RequestOutstandingForConnectionAsync(int port, string callFrom, string callTo, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var f = await RequestAsync(new AgwFrame(port, 'Y', callFrom, callTo), r => r.DataKind == 'Y' && r.Port == port, timeout, ct).ConfigureAwait(false);
        return f.Payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(f.Payload) : 0;
    }

    // ---- Fire and forget commands ----

    /// <summary>'m': start receiving monitor frames (U/I/S/T). Sent once; Dire Wolf treats it as a toggle.</summary>
    public async Task EnableMonitoringAsync(CancellationToken ct = default)
    {
        if (MonitoringEnabled) return;
        await SendAsync(new AgwFrame(0, 'm'), ct).ConfigureAwait(false);
        MonitoringEnabled = true;
    }

    /// <summary>'k': start receiving raw frames ('K'). Sent once; Dire Wolf treats it as a toggle.</summary>
    public async Task EnableRawFramesAsync(CancellationToken ct = default)
    {
        if (RawFramesEnabled) return;
        await SendAsync(new AgwFrame(0, 'k'), ct).ConfigureAwait(false);
        RawFramesEnabled = true;
    }

    public Task UnregisterCallsignAsync(int port, string callsign, CancellationToken ct = default) => SendAsync(new AgwFrame(port, 'x', callsign), ct);

    /// <summary>'C': start an AX.25 connection.</summary>
    public Task ConnectStationAsync(int port, string myCall, string remoteCall, CancellationToken ct = default) =>
        SendAsync(new AgwFrame(port, 'C', myCall, remoteCall), ct);

    /// <summary>'v': connect through digipeaters (1..7).</summary>
    public Task ConnectStationViaAsync(int port, string myCall, string remoteCall, IReadOnlyList<string> via, CancellationToken ct = default)
    {
        if (via.Count < 1 || via.Count > 7) throw new ArgumentException("Between 1 and 7 digipeaters.", nameof(via));
        return SendAsync(new AgwFrame(port, 'v', myCall, remoteCall, DigiList(via)), ct);
    }

    /// <summary>'c': connect with a non-standard PID.</summary>
    public Task ConnectStationWithPidAsync(int port, string myCall, string remoteCall, byte pid, CancellationToken ct = default) =>
        SendAsync(new AgwFrame(port, 'c', myCall, remoteCall, null, pid), ct);

    /// <summary>'D': send data on an established connection.</summary>
    public Task SendConnectedDataAsync(int port, string myCall, string remoteCall, byte[] data, byte pid = 0xF0, CancellationToken ct = default) =>
        SendAsync(new AgwFrame(port, 'D', myCall, remoteCall, data, pid), ct);

    /// <summary>'d': disconnect.</summary>
    public Task DisconnectStationAsync(int port, string myCall, string remoteCall, CancellationToken ct = default) =>
        SendAsync(new AgwFrame(port, 'd', myCall, remoteCall), ct);

    /// <summary>'M': transmit a UI frame without digipeater path.</summary>
    public Task SendUnprotoAsync(int port, string from, string to, byte[] info, byte pid = 0xF0, CancellationToken ct = default) =>
        SendAsync(new AgwFrame(port, 'M', from, to, info, pid), ct);

    /// <summary>'V': transmit a UI frame via digipeaters.</summary>
    public Task SendUnprotoViaAsync(int port, string from, string to, IReadOnlyList<string> via, byte[] info, byte pid = 0xF0, CancellationToken ct = default)
    {
        if (via.Count > 8) throw new ArgumentException("At most 8 digipeaters.", nameof(via));
        var digis = DigiList(via);
        var data = new byte[digis.Length + info.Length];
        digis.CopyTo(data, 0);
        info.CopyTo(data, digis.Length);
        return SendAsync(new AgwFrame(port, 'V', from, to, data, pid), ct);
    }

    /// <summary>'K': transmit a raw AX.25 frame (no FCS).</summary>
    public Task SendRawFrameAsync(int port, byte[] ax25Frame, CancellationToken ct = default)
    {
        var data = new byte[ax25Frame.Length + 1];
        data[0] = (byte)(port << 4);
        ax25Frame.CopyTo(data, 1);
        return SendAsync(new AgwFrame(port, 'K', "", "", data), ct);
    }

    /// <summary>'H': ask for heard stations (Dire Wolf may not answer).</summary>
    public Task RequestHeardAsync(int port, CancellationToken ct = default) => SendAsync(new AgwFrame(port, 'H'), ct);

    private static byte[] DigiList(IReadOnlyList<string> via)
    {
        var data = new byte[1 + 10 * via.Count];
        data[0] = (byte)via.Count;
        for (int i = 0; i < via.Count; i++)
        {
            var b = Encoding.ASCII.GetBytes(via[i].Trim().ToUpperInvariant());
            b.AsSpan(0, Math.Min(9, b.Length)).CopyTo(data.AsSpan(1 + 10 * i));
        }
        return data;
    }
}
