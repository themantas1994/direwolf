using System.Diagnostics;
using System.Net.Sockets;

namespace DireWolfGui.Core.Net;

public sealed record ConnectionTestResult(bool Success, string Summary, string Detail, TimeSpan Elapsed);

/// <summary>Connection checks for the settings screens, with friendly error messages.</summary>
public static class ConnectionTester
{
    public static async Task<ConnectionTestResult> TestTcpAsync(string host, int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return new ConnectionTestResult(true, $"Connected to {host}:{port}.", $"TCP connection to {tcp.Client.RemoteEndPoint} succeeded.", sw.Elapsed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Failure(host, port, ex, sw.Elapsed);
        }
    }

    /// <summary>Connects to an AGW server and asks for its version and radio ports.</summary>
    public static async Task<ConnectionTestResult> TestAgwAsync(string host, int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var to = timeout ?? TimeSpan.FromSeconds(5);
        await using var client = new AgwClient();
        try
        {
            await client.ConnectAsync(host, port, to, ct).ConfigureAwait(false);
            var (major, minor) = await client.RequestVersionAsync(to, ct).ConfigureAwait(false);
            var ports = await client.RequestPortInfoAsync(to, ct).ConfigureAwait(false);
            string detail = $"AGW version {major}.{minor}; {ports.Count} radio port(s)" +
                            (ports.Descriptions.Count > 0 ? ": " + string.Join("; ", ports.Descriptions) : ".");
            return new ConnectionTestResult(true, $"AGW server at {host}:{port} is working.", detail, sw.Elapsed);
        }
        catch (TimeoutException ex) when (client.IsConnected)
        {
            return new ConnectionTestResult(false, $"{host}:{port} accepted the connection but did not answer like an AGW server.", ex.Message, sw.Elapsed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Failure(host, port, ex, sw.Elapsed);
        }
    }

    /// <summary>KISS has no handshake: connect and check the server keeps the connection open briefly.</summary>
    public static async Task<ConnectionTestResult> TestKissAsync(string host, int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var client = new KissTcpClient();
        string? closed = null;
        client.Disconnected += r => closed = r;
        try
        {
            await client.ConnectAsync(host, port, timeout, ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
            if (closed != null)
                return new ConnectionTestResult(false, $"{host}:{port} closed the connection immediately.", closed, sw.Elapsed);
            return new ConnectionTestResult(true, $"KISS TCP port {host}:{port} accepted the connection.", "KISS has no handshake; the connection stayed open.", sw.Elapsed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Failure(host, port, ex, sw.Elapsed);
        }
    }

    public static string Describe(Exception ex, string host, int port)
    {
        var se = ex as SocketException ?? ex.InnerException as SocketException;
        if (se != null)
        {
            return se.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => $"Connection refused by {host}:{port}. Is Dire Wolf running, and is this port enabled in its configuration?",
                SocketError.TimedOut => $"No answer from {host}:{port} (timed out). Check the address and any firewall.",
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => $"Host \"{host}\" was not found. Check the spelling of the address.",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => $"{host} cannot be reached from this computer.",
                SocketError.AddressNotAvailable => $"The address {host}:{port} is not valid here.",
                _ => $"Could not connect to {host}:{port}: {se.Message}",
            };
        }
        return ex switch
        {
            TimeoutException or OperationCanceledException => $"No answer from {host}:{port} (timed out). Check the address and any firewall.",
            ArgumentOutOfRangeException => $"Port {port} is not valid (1-65535).",
            IOException io => $"The connection to {host}:{port} failed: {io.Message}",
            _ => $"Could not connect to {host}:{port}: {ex.Message}",
        };
    }

    private static ConnectionTestResult Failure(string host, int port, Exception ex, TimeSpan elapsed) =>
        new(false, Describe(ex, host, port), ex.GetType().Name + ": " + ex.Message, elapsed);
}
