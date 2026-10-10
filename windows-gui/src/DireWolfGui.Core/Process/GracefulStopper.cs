using System.Runtime.InteropServices;
using SysProcess = System.Diagnostics.Process;

namespace DireWolfGui.Core.Process;

/// <summary>
/// Asks direwolf to quit the way a user pressing Ctrl+C would, so it prints "QRT" and releases PTT.
/// Windows: attach to the child's (hidden) console and send CTRL_C_EVENT. Elsewhere: SIGINT.
/// </summary>
public static class GracefulStopper
{
    // Only one console attach may happen at a time in this process.
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    /// <summary>Returns true if the process exited within <paramref name="timeout"/> after the interrupt.</summary>
    public static async Task<bool> TryStopAsync(SysProcess process, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (HasExited(process)) return true;
        if (OperatingSystem.IsWindows()) return await StopWindowsAsync(process, timeout, ct).ConfigureAwait(false);
        if (kill(process.Id, SIGINT) != 0) return HasExited(process);
        return await WaitAsync(process, timeout, ct).ConfigureAwait(false);
    }

    private static async Task<bool> StopWindowsAsync(SysProcess process, TimeSpan timeout, CancellationToken ct)
    {
        await ConsoleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Run on a worker thread: the attach sequence blocks briefly and must not run on the UI thread.
            return await Task.Run(async () =>
            {
                FreeConsole();
                if (!AttachConsole((uint)process.Id))
                    return HasExited(process);
                try
                {
                    SetConsoleCtrlHandler(IntPtr.Zero, true);   // do not kill ourselves
                    if (!GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)) return HasExited(process);
                    return await WaitAsync(process, timeout, ct).ConfigureAwait(false);
                }
                finally
                {
                    FreeConsole();
                    // The Ctrl+C event is delivered asynchronously; keep ignoring it a moment before restoring.
                    await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    SetConsoleCtrlHandler(IntPtr.Zero, false);
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            ConsoleLock.Release();
        }
    }

    private static async Task<bool> WaitAsync(SysProcess p, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return HasExited(p); }
    }

    private static bool HasExited(SysProcess p)
    {
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private const int SIGINT = 2;
    private const uint CTRL_C_EVENT = 0;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}
