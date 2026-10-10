using System.Net.Sockets;

namespace DireWolfGui.Core.Net;

/// <summary>A KISS frame: port (high nibble of the command byte), command (low nibble, 0 = data) and payload.</summary>
public sealed record KissFrame(int Port, int Command, byte[] Data);

/// <summary>KISS framing (FEND/FESC escaping) and a stateful decoder that copes with partial reads.</summary>
public sealed class KissCodec
{
    public const byte FEND = 0xC0, FESC = 0xDB, TFEND = 0xDC, TFESC = 0xDD;
    public const int MaxFrameLength = 4096;

    private readonly List<byte> _current = new();
    private bool _inFrame, _escape, _overflow;

    public static byte[] Encode(int port, ReadOnlySpan<byte> data, int command = 0)
    {
        var o = new List<byte>(data.Length + 8) { FEND, (byte)(((port & 0x0F) << 4) | (command & 0x0F)) };
        foreach (var b in data)
        {
            if (b == FEND) { o.Add(FESC); o.Add(TFEND); }
            else if (b == FESC) { o.Add(FESC); o.Add(TFESC); }
            else o.Add(b);
        }
        o.Add(FEND);
        return o.ToArray();
    }

    /// <summary>Feeds received bytes; returns the frames completed by them.</summary>
    public IReadOnlyList<KissFrame> Push(ReadOnlySpan<byte> bytes)
    {
        List<KissFrame>? frames = null;
        foreach (var b in bytes)
        {
            if (b == FEND)
            {
                if (_inFrame && _current.Count > 0 && !_overflow)
                {
                    byte cmd = _current[0];
                    (frames ??= new()).Add(new KissFrame(cmd >> 4, cmd & 0x0F, _current.Skip(1).ToArray()));
                }
                _current.Clear();
                _inFrame = true;
                _escape = _overflow = false;
                continue;
            }
            if (!_inFrame) continue;
            byte v = b;
            if (_escape)
            {
                _escape = false;
                v = b == TFEND ? FEND : b == TFESC ? FESC : b;
            }
            else if (b == FESC) { _escape = true; continue; }
            if (_current.Count >= MaxFrameLength) { _overflow = true; continue; }
            _current.Add(v);
        }
        return frames ?? (IReadOnlyList<KissFrame>)Array.Empty<KissFrame>();
    }
}

/// <summary>KISS over TCP client (Dire Wolf KISSPORT). Raises <see cref="FrameReceived"/> on a background task.</summary>
public sealed class KissTcpClient : IAsyncDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task? _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _closed;

    public bool IsConnected => _stream != null;
    public event Action<KissFrame>? FrameReceived;
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(string host, int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (IsConnected) throw new InvalidOperationException("Already connected.");
        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try { await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { tcp.Dispose(); throw new TimeoutException($"Timed out connecting to {host}:{port}."); }
        catch { tcp.Dispose(); throw; }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _closed = 0;
        var stream = _stream;
        _reader = Task.Run(() => ReadLoopAsync(stream));
    }

    private async Task ReadLoopAsync(NetworkStream stream)
    {
        var codec = new KissCodec();
        var buf = new byte[4096];
        string reason = "Connection closed by the server.";
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf).ConfigureAwait(false)) > 0)
                foreach (var f in codec.Push(buf.AsSpan(0, n)))
                {
                    try { FrameReceived?.Invoke(f); } catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
                }
        }
        catch (IOException ex) { reason = "Connection lost: " + (ex.InnerException?.Message ?? ex.Message); }
        catch (ObjectDisposedException) { reason = "Disconnected."; }
        Close(reason, stream);
    }

    private void Close(string reason, NetworkStream stream)
    {
        if (!ReferenceEquals(stream, _stream)) return;
        _stream = null;
        stream.Dispose();
        _tcp?.Dispose();
        _tcp = null;
        if (Interlocked.Exchange(ref _closed, 1) == 0) Disconnected?.Invoke(reason);
    }

    /// <summary>Sends an AX.25 frame (no FCS) as a KISS data frame on <paramref name="port"/>.</summary>
    public async Task SendFrameAsync(int port, byte[] ax25Frame, CancellationToken ct = default)
    {
        var s = _stream ?? throw new InvalidOperationException("Not connected to the KISS server.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await s.WriteAsync(KissCodec.Encode(port, ax25Frame), ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public async Task DisconnectAsync()
    {
        var s = _stream;
        if (s == null) return;
        s.Dispose();
        if (_reader != null) await _reader.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
