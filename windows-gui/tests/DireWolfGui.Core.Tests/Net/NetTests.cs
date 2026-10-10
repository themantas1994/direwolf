using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DireWolfGui.Core.Net;
using DireWolfGui.Core.Packets;

namespace DireWolfGui.Core.Tests.Net;

/// <summary>Minimal in-test TCP server: hands each accepted socket to a handler.</summary>
internal sealed class LoopbackServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _accept;
    private readonly CancellationTokenSource _cts = new();

    public LoopbackServer(Func<NetworkStream, CancellationToken, Task> handler)
    {
        _listener.Start();
        _accept = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(async () => { using (c) { try { await handler(c.GetStream(), _cts.Token); } catch { } } });
                }
            }
            catch { }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static int UnusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await _accept;
    }
}

public class AgwFrameTests
{
    [Fact]
    public void EncodeDecodeRoundTrip()
    {
        var f = new AgwFrame(1, 'V', "N0TEST-1", "APDW18", new byte[] { 1, 2, 3 }, 0xF0, 7);
        var b = f.Encode();
        Assert.Equal(39, b.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(28)));
        Assert.True(AgwFrame.TryDecode(b, out var d, out int used));
        Assert.Equal(39, used);
        Assert.Equal(f.Port, d!.Port);
        Assert.Equal('V', d.DataKind);
        Assert.Equal("N0TEST-1", d.CallFrom);
        Assert.Equal("APDW18", d.CallTo);
        Assert.Equal((byte)0xF0, d.Pid);
        Assert.Equal(7u, d.User);
        Assert.Equal(new byte[] { 1, 2, 3 }, d.Payload);
        Assert.False(AgwFrame.TryDecode(b.AsSpan(0, 38), out _, out _));   // partial
    }

    [Fact]
    public void RejectsOversizedLength()
    {
        var b = new AgwFrame(0, 'U').Encode();
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 10_000_000);
        Assert.Throws<InvalidDataException>(() => AgwFrame.TryDecode(b, out _, out _));
    }

    [Fact]
    public void PortInfo() =>
        Assert.Equal(new[] { "Port1 stdin soundcard mono", "Port2 Internet Gateway" },
            AgwPortInfo.Parse("2;Port1 stdin soundcard mono;Port2 Internet Gateway;\0").Descriptions);
}

public class AgwClientTests
{
    private static async Task<AgwFrame> ReadFrame(NetworkStream s, CancellationToken ct)
    {
        var h = new byte[36];
        await s.ReadExactlyAsync(h, ct);
        var f = AgwFrame.DecodeHeader(h, out uint len);
        var d = new byte[len];
        await s.ReadExactlyAsync(d, ct);
        return f with { Data = d };
    }

    [Fact]
    public async Task VersionPortsRegisterAndPartialWrites()
    {
        var received = new List<AgwFrame>();
        await using var server = new LoopbackServer(async (s, ct) =>
        {
            while (true)
            {
                var f = await ReadFrame(s, ct);
                lock (received) received.Add(f);
                byte[] reply = f.DataKind switch
                {
                    'R' => new AgwFrame(0, 'R', Data: BitConverter.GetBytes(2005).Concat(BitConverter.GetBytes(127)).ToArray()).Encode(),
                    'G' => new AgwFrame(0, 'G', Data: Encoding.ASCII.GetBytes("1;Port1 stdin soundcard mono;\0")).Encode(),
                    'X' => new AgwFrame(f.Port, 'X', f.CallFrom, Data: new byte[] { 1 }).Encode(),
                    'y' => new AgwFrame(f.Port, 'y', Data: BitConverter.GetBytes(4)).Encode(),
                    _ => Array.Empty<byte>(),
                };
                // Dribble the reply one byte at a time to exercise partial reads.
                foreach (var b in reply) { await s.WriteAsync(new[] { b }, ct); await s.FlushAsync(ct); }
            }
        });
        await using var c = new AgwClient();
        await c.ConnectAsync("127.0.0.1", server.Port);
        Assert.Equal((2005, 127), await c.RequestVersionAsync());
        var ports = await c.RequestPortInfoAsync();
        Assert.Equal(1, ports.Count);
        Assert.True(await c.RegisterCallsignAsync(0, "N0TEST"));
        Assert.Equal(4, await c.RequestOutstandingFramesAsync(0));
        await c.EnableMonitoringAsync();
        await c.EnableMonitoringAsync();   // toggle protection: sent only once
        await c.SendUnprotoViaAsync(0, "N0TEST", "APDW18", new[] { "WIDE1-1", "WIDE2-1" }, Encoding.ASCII.GetBytes(">hi"));
        await c.SendRawFrameAsync(1, new byte[] { 9, 9 });
        await Task.Delay(200);
        lock (received)
        {
            Assert.Single(received, f => f.DataKind == 'm');
            var v = received.Single(f => f.DataKind == 'V');
            Assert.Equal(2, v.Payload[0]);
            Assert.Equal("WIDE1-1", Encoding.ASCII.GetString(v.Payload, 1, 10).TrimEnd('\0'));
            Assert.Equal("WIDE2-1", Encoding.ASCII.GetString(v.Payload, 11, 10).TrimEnd('\0'));
            Assert.Equal(">hi", Encoding.ASCII.GetString(v.Payload, 21, 3));
            var k = received.Single(f => f.DataKind == 'K');
            Assert.Equal(new byte[] { 0x10, 9, 9 }, k.Payload);
        }
    }

    [Fact]
    public async Task ConnectionRefusedIsReported()
    {
        await using var c = new AgwClient();
        var ex = await Assert.ThrowsAnyAsync<SocketException>(() => c.ConnectAsync("127.0.0.1", LoopbackServer.UnusedPort()));
        Assert.Contains("refused", ConnectionTester.Describe(ex, "127.0.0.1", 1), StringComparison.OrdinalIgnoreCase);
        Assert.False(c.IsConnected);
    }

    [Fact]
    public async Task ServerDropRaisesDisconnectedAndFailsPendingRequest()
    {
        await using var server = new LoopbackServer(async (s, ct) =>
        {
            await ReadFrame(s, ct);   // read the request, then hang up without answering
        });
        await using var c = new AgwClient();
        var disconnected = new TaskCompletionSource<string>();
        c.Disconnected += r => disconnected.TrySetResult(r);
        await c.ConnectAsync("127.0.0.1", server.Port);
        await Assert.ThrowsAsync<IOException>(() => c.RequestVersionAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("closed", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(c.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.EnableMonitoringAsync());
    }

    [Fact]
    public async Task OversizedLengthDisconnects()
    {
        await using var server = new LoopbackServer(async (s, ct) =>
        {
            var b = new AgwFrame(0, 'U').Encode();
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 50_000_000);
            await s.WriteAsync(b, ct);
            await Task.Delay(5000, ct);
        });
        await using var c = new AgwClient();
        var disconnected = new TaskCompletionSource<string>();
        c.Disconnected += r => disconnected.TrySetResult(r);
        await c.ConnectAsync("127.0.0.1", server.Port);
        Assert.Contains("too large", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RequestTimesOut()
    {
        await using var server = new LoopbackServer(async (s, ct) => { await ReadFrame(s, ct); await Task.Delay(10000, ct); });
        await using var c = new AgwClient();
        await c.ConnectAsync("127.0.0.1", server.Port);
        await Assert.ThrowsAsync<TimeoutException>(() => c.RequestVersionAsync(TimeSpan.FromMilliseconds(300)));
    }
}

public class AgwTerminalSessionTests
{
    [Fact]
    public async Task ConnectSendReceiveDisconnect()
    {
        var gotData = new TaskCompletionSource<string>();
        await using var server = new LoopbackServer(async (s, ct) =>
        {
            var h = new byte[36];
            while (true)
            {
                await s.ReadExactlyAsync(h, ct);
                var f = AgwFrame.DecodeHeader(h, out uint len);
                var d = new byte[len];
                await s.ReadExactlyAsync(d, ct);
                switch (f.DataKind)
                {
                    case 'X': await s.WriteAsync(new AgwFrame(f.Port, 'X', f.CallFrom, Data: new byte[] { 1 }).Encode(), ct); break;
                    case 'C':
                        await s.WriteAsync(new AgwFrame(f.Port, 'C', f.CallTo, f.CallFrom, Encoding.ASCII.GetBytes($"*** CONNECTED With Station {f.CallTo}\r\0")).Encode(), ct);
                        await s.WriteAsync(new AgwFrame(f.Port, 'D', f.CallTo, f.CallFrom, Encoding.ASCII.GetBytes("Welcome\r"), 0xF0).Encode(), ct);
                        break;
                    case 'D':
                        gotData.TrySetResult(Encoding.ASCII.GetString(d));
                        break;
                    case 'd': await s.WriteAsync(new AgwFrame(f.Port, 'd', f.CallTo, f.CallFrom, Encoding.ASCII.GetBytes($"*** DISCONNECTED From Station {f.CallTo}\r\0")).Encode(), ct); break;
                }
            }
        });
        await using var c = new AgwClient();
        await c.ConnectAsync("127.0.0.1", server.Port);
        using var t = new AgwTerminalSession(c, 0, "n0test");
        var notes = new List<string>();
        var text = new TaskCompletionSource<string>();
        var states = new List<TerminalState>();
        t.Notification += n => { lock (notes) notes.Add(n); };
        t.DataReceived += d => text.TrySetResult(d);
        t.StateChanged += s => { lock (states) states.Add(s); };
        await t.ConnectAsync("N0BBS");
        Assert.True(t.IsRegistered);
        Assert.Equal("Welcome\n", await text.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TerminalState.Connected, t.State);
        await t.SendLineAsync("help");
        Assert.Equal("help\r", await gotData.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await t.DisconnectAsync();
        for (int i = 0; i < 50 && t.State != TerminalState.Disconnected; i++) await Task.Delay(50);
        Assert.Equal(TerminalState.Disconnected, t.State);
        lock (notes)
        {
            Assert.Contains("*** CONNECTED With Station N0BBS", notes);
            Assert.Contains("*** DISCONNECTED From Station N0BBS", notes);
        }
        lock (states) Assert.Equal(new[] { TerminalState.Connecting, TerminalState.Connected, TerminalState.Disconnecting, TerminalState.Disconnected }, states);
    }
}

public class KissTests
{
    [Fact]
    public void EncodeEscapesAndDecoderHandlesPartialFrames()
    {
        var data = new byte[] { 1, KissCodec.FEND, 2, KissCodec.FESC, 3 };
        var enc = KissCodec.Encode(2, data);
        Assert.Equal(new byte[] { 0xC0, 0x20, 1, 0xDB, 0xDC, 2, 0xDB, 0xDD, 3, 0xC0 }, enc);
        var dec = new KissCodec();
        var frames = new List<KissFrame>();
        foreach (var b in enc.Concat(KissCodec.Encode(0, new byte[] { 7 }))) frames.AddRange(dec.Push(new[] { b }));
        Assert.Equal(2, frames.Count);
        Assert.Equal(2, frames[0].Port);
        Assert.Equal(0, frames[0].Command);
        Assert.Equal(data, frames[0].Data);
        Assert.Equal(new byte[] { 7 }, frames[1].Data);
        Assert.Empty(dec.Push(new byte[] { 0xC0, 0xC0, 0xC0 }));   // empty frames ignored
    }

    [Fact]
    public async Task TcpClientSendsAndReceives()
    {
        var serverGot = new TaskCompletionSource<byte[]>();
        var frame = Ax25Frame.CreateUi("N0A", "APRS", null, ">x").Encode();
        await using var server = new LoopbackServer(async (s, ct) =>
        {
            var enc = KissCodec.Encode(0, frame);
            await s.WriteAsync(enc.AsMemory(0, 5), ct);
            await Task.Delay(50, ct);
            await s.WriteAsync(enc.AsMemory(5), ct);
            var buf = new byte[256];
            var codec = new KissCodec();
            while (true)
            {
                int n = await s.ReadAsync(buf, ct);
                if (n == 0) return;
                foreach (var f in codec.Push(buf.AsSpan(0, n))) serverGot.TrySetResult(f.Data);
            }
        });
        await using var c = new KissTcpClient();
        var got = new TaskCompletionSource<KissFrame>();
        c.FrameReceived += f => got.TrySetResult(f);
        await c.ConnectAsync("127.0.0.1", server.Port);
        var rx = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(frame, rx.Data);
        await c.SendFrameAsync(0, frame);
        Assert.Equal(frame, await serverGot.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}

public class ConnectionTesterTests
{
    [Fact]
    public async Task RefusedTimeoutAndHostNotFound()
    {
        var r = await ConnectionTester.TestTcpAsync("127.0.0.1", LoopbackServer.UnusedPort());
        Assert.False(r.Success);
        Assert.Contains("refused", r.Summary);
        var k = await ConnectionTester.TestKissAsync("127.0.0.1", LoopbackServer.UnusedPort());
        Assert.False(k.Success);
        var h = await ConnectionTester.TestTcpAsync("no-such-host.invalid", 8000, TimeSpan.FromSeconds(10));
        Assert.False(h.Success);
        Assert.Contains("not found", h.Summary);
        Assert.Contains("timed out", ConnectionTester.Describe(new TimeoutException(), "h", 1));
    }

    [Fact]
    public async Task AgwTestAgainstSilentServerSaysNotAgw()
    {
        await using var server = new LoopbackServer(async (s, ct) => await Task.Delay(10000, ct));
        var r = await ConnectionTester.TestAgwAsync("127.0.0.1", server.Port, TimeSpan.FromMilliseconds(500));
        Assert.False(r.Success);
        Assert.Contains("did not answer like an AGW server", r.Summary);
        var t = await ConnectionTester.TestTcpAsync("127.0.0.1", server.Port);
        Assert.True(t.Success);
        var k = await ConnectionTester.TestKissAsync("127.0.0.1", server.Port);
        Assert.True(k.Success);
    }
}
