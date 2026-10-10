using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SysProcess = System.Diagnostics.Process;

namespace DireWolfGui.Core.Process;

public sealed class DireWolfOutputLineEventArgs(string line, bool isStandardError, DateTimeOffset time) : EventArgs
{
    /// <summary>The line exactly as printed (not yet redacted; consumers that store it must redact).</summary>
    public string Line { get; } = line;
    public bool IsStandardError { get; } = isStandardError;
    public DateTimeOffset Time { get; } = time;
}

public sealed class DireWolfStateChangedEventArgs(DireWolfState oldState, DireWolfState newState) : EventArgs
{
    public DireWolfState OldState { get; } = oldState;
    public DireWolfState NewState { get; } = newState;
}

public sealed class DireWolfExitedEventArgs(int? exitCode, bool expected, DireWolfDiagnosis? diagnosis, DateTimeOffset time) : EventArgs
{
    public int? ExitCode { get; } = exitCode;
    /// <summary>True when the exit followed <see cref="DireWolfProcessController.StopAsync"/>.</summary>
    public bool Expected { get; } = expected;
    /// <summary>Explanation for unexpected exits (null for expected ones).</summary>
    public DireWolfDiagnosis? Diagnosis { get; } = diagnosis;
    public DateTimeOffset Time { get; } = time;
}

public enum StopOutcome { NotRunning, Graceful, Killed }

public sealed record StopResult(StopOutcome Outcome, string Message)
{
    /// <summary>True when direwolf had to be killed, so it could not unkey PTT itself.</summary>
    public bool PttMayBeKeyed => Outcome == StopOutcome.Killed;
}

public sealed record ProcessResourceSample(DateTimeOffset Time, double CpuPercent, long WorkingSetBytes, int ThreadCount);

/// <summary>Thrown when direwolf could not be started at all (missing executable, invalid options, OS error).</summary>
public sealed class DireWolfStartException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Starts, monitors and stops one direwolf process. Output is read line by line on background threads and
/// raised through <see cref="OutputLine"/> (on those threads). All public methods are safe to call from a UI thread.
/// </summary>
public sealed class DireWolfProcessController : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, DireWolfProcessController> RunningConfigs = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private readonly Queue<string> _recent = new();
    private readonly TimeProvider _time;
    private SysProcess? _process;
    private Task? _monitor;
    private string? _registeredConfig;
    private volatile bool _stopRequested;
    private DireWolfState _state = DireWolfState.Stopped;
    private (DateTimeOffset Wall, TimeSpan Cpu)? _lastCpu;

    public DireWolfProcessController(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    public DireWolfState State { get { lock (_lock) return _state; } }
    public int? ProcessId { get; private set; }
    public DateTimeOffset? StartTime { get; private set; }
    public TimeSpan? Uptime => State is DireWolfState.Running or DireWolfState.Stopping && StartTime is { } s ? _time.GetUtcNow() - s : null;
    public int? LastExitCode { get; private set; }
    public DireWolfDiagnosis? LastDiagnosis { get; private set; }
    public DireWolfLaunchOptions? LastOptions { get; private set; }
    public IReadOnlyList<string> CommandLineArguments { get; private set; } = Array.Empty<string>();

    /// <summary>How many recent lines are kept for diagnosing an unexpected exit.</summary>
    public int RecentLineCapacity { get; init; } = 200;

    public event EventHandler<DireWolfStateChangedEventArgs>? StateChanged;
    public event EventHandler<DireWolfOutputLineEventArgs>? OutputLine;
    public event EventHandler<DireWolfExitedEventArgs>? Exited;

    /// <summary>Config files currently run by any controller in this process.</summary>
    public static IReadOnlyCollection<string> ManagedConfigFiles => RunningConfigs.Keys.ToArray();

    public IReadOnlyList<string> GetRecentLines() { lock (_recent) return _recent.ToArray(); }

    public async Task StartAsync(DireWolfLaunchOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        DireWolfState previous;
        lock (_lock)
        {
            if (_state is DireWolfState.Starting or DireWolfState.Running or DireWolfState.Stopping)
                throw new InvalidOperationException($"Dire Wolf is already running (process {ProcessId}).");
            previous = _state;
            _state = DireWolfState.Starting;   // claim atomically so two concurrent starts cannot both proceed
        }
        RaiseStateChanged(previous, DireWolfState.Starting);
        // Make sure a previous exit has been fully processed.
        if (_monitor != null) await _monitor.ConfigureAwait(false);

        IReadOnlyList<string> args;
        try { args = DireWolfLaunchOptions.BuildArgumentList(options); }
        catch (ArgumentException ex) { Fail(ex.Message); throw new DireWolfStartException(ex.Message, ex); }

        string exe = options.ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            string msg = $"Dire Wolf program not found: \"{exe}\". Choose the location of direwolf{(OperatingSystem.IsWindows() ? ".exe" : "")}.";
            Fail(msg);
            throw new DireWolfStartException(msg);
        }

        string configKey = Path.GetFullPath(options.ConfigPath, options.EffectiveWorkingDirectory);
        if (!RunningConfigs.TryAdd(configKey, this))
        {
            string msg = $"Dire Wolf is already running with configuration \"{configKey}\" from this program.";
            Fail(msg);
            throw new DireWolfStartException(msg);
        }
        _registeredConfig = configKey;

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = options.StandardInputAudio,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = options.EffectiveWorkingDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        lock (_recent) _recent.Clear();
        _stopRequested = false;
        LastDiagnosis = null;
        LastExitCode = null;
        LastOptions = options;
        CommandLineArguments = args;

        SysProcess p;
        try
        {
            p = await Task.Run(() => SysProcess.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null."), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            RunningConfigs.TryRemove(configKey, out _);
            _registeredConfig = null;
            string msg = $"Could not start Dire Wolf: {ex.Message}";
            Fail(msg);
            throw new DireWolfStartException(msg, ex);
        }

        _process = p;
        ProcessId = p.Id;
        StartTime = _time.GetUtcNow();
        _lastCpu = null;
        var outTask = Task.Factory.StartNew(() => ReadLines(p.StandardOutput, false), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var errTask = Task.Factory.StartNew(() => ReadLines(p.StandardError, true), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        SetState(DireWolfState.Running);
        _monitor = Task.Run(() => MonitorAsync(p, outTask, errTask));
    }

    private void ReadLines(StreamReader reader, bool isErr)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lock (_recent)
                {
                    _recent.Enqueue(line);
                    while (_recent.Count > RecentLineCapacity) _recent.Dequeue();
                }
                try { OutputLine?.Invoke(this, new DireWolfOutputLineEventArgs(line, isErr, _time.GetUtcNow())); }
                catch (Exception ex) { Trace.WriteLine($"OutputLine handler failed: {ex}"); }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task MonitorAsync(SysProcess p, Task outTask, Task errTask)
    {
        try { await p.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
        // Output may still be buffered; give readers a moment to drain (child processes could keep pipes open).
        await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(2000)).ConfigureAwait(false);
        int? code = null;
        try { code = p.ExitCode; } catch (InvalidOperationException) { }
        bool expected = _stopRequested;
        var diagnosis = expected ? null : DireWolfDiagnostics.Diagnose(GetRecentLines(), code);
        LastExitCode = code;
        LastDiagnosis = diagnosis;
        if (_registeredConfig != null) RunningConfigs.TryRemove(_registeredConfig, out _);
        _registeredConfig = null;
        _process = null;
        p.Dispose();
        SetState(expected ? DireWolfState.Stopped : DireWolfState.Failed);
        try { Exited?.Invoke(this, new DireWolfExitedEventArgs(code, expected, diagnosis, _time.GetUtcNow())); }
        catch (Exception ex) { Trace.WriteLine($"Exited handler failed: {ex}"); }
    }

    /// <summary>
    /// Stops direwolf: interrupt (Ctrl+C / SIGINT) first so it releases PTT, then kills the process tree
    /// if it has not exited within <paramref name="timeout"/> (default 5 s).
    /// </summary>
    public async Task<StopResult> StopAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        SysProcess? p;
        Task? monitor;
        lock (_lock)
        {
            p = _process;
            monitor = _monitor;
            if (p == null || _state is DireWolfState.Stopped or DireWolfState.Failed)
                return new StopResult(StopOutcome.NotRunning, "Dire Wolf is not running.");
        }
        _stopRequested = true;
        SetState(DireWolfState.Stopping);
        bool graceful;
        try { graceful = await GracefulStopper.TryStopAsync(p, timeout ?? TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Trace.WriteLine($"Graceful stop failed: {ex}"); graceful = false; }

        StopResult result;
        if (graceful) result = new StopResult(StopOutcome.Graceful, "Dire Wolf stopped.");
        else
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            result = new StopResult(StopOutcome.Killed,
                "Dire Wolf did not respond to the stop request and was terminated. It could not release PTT itself: check that the transmitter is not keyed.");
        }
        if (monitor != null) await monitor.ConfigureAwait(false);
        return result;
    }

    public async Task RestartAsync(TimeSpan? stopTimeout = null, CancellationToken ct = default)
    {
        var options = LastOptions ?? throw new InvalidOperationException("Dire Wolf has not been started before.");
        await StopAsync(stopTimeout, ct).ConfigureAwait(false);
        await StartAsync(options, ct).ConfigureAwait(false);
    }

    /// <summary>TEST / DIAGNOSTIC ONLY (see <see cref="DireWolfLaunchOptions.StandardInputAudio"/>): writes raw audio to direwolf's stdin.</summary>
    public async Task WriteStandardInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var p = _process ?? throw new InvalidOperationException("Dire Wolf is not running.");
        if (LastOptions?.StandardInputAudio != true) throw new InvalidOperationException("StandardInputAudio was not enabled.");
        var s = p.StandardInput.BaseStream;
        await s.WriteAsync(data, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>TEST / DIAGNOSTIC ONLY: closes stdin; direwolf then prints "End of file on stdin" and exits.</summary>
    public void CloseStandardInput()
    {
        try { _process?.StandardInput.Close(); } catch (InvalidOperationException) { } catch (IOException) { }
    }

    /// <summary>CPU (percent of the whole machine since the previous sample) and memory use; null when not running.</summary>
    public ProcessResourceSample? SampleResources()
    {
        var p = _process;
        if (p == null) return null;
        try
        {
            p.Refresh();
            if (p.HasExited) return null;
            var now = _time.GetUtcNow();
            var cpu = p.TotalProcessorTime;
            double percent = 0;
            var prev = _lastCpu ?? (StartTime ?? now, TimeSpan.Zero);
            double wall = (now - prev.Wall).TotalMilliseconds;
            if (wall > 0) percent = Math.Clamp((cpu - prev.Cpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100.0, 0, 100);
            _lastCpu = (now, cpu);
            return new ProcessResourceSample(now, percent, p.WorkingSet64, p.Threads.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private void Fail(string message)
    {
        LastDiagnosis = new DireWolfDiagnosis(message, Array.Empty<string>());
        SetState(DireWolfState.Failed);
    }

    private void SetState(DireWolfState s)
    {
        DireWolfState old;
        lock (_lock)
        {
            old = _state;
            if (old == s) return;
            _state = s;
        }
        RaiseStateChanged(old, s);
    }

    private void RaiseStateChanged(DireWolfState old, DireWolfState s)
    {
        try { StateChanged?.Invoke(this, new DireWolfStateChangedEventArgs(old, s)); }
        catch (Exception ex) { Trace.WriteLine($"StateChanged handler failed: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (State is DireWolfState.Running or DireWolfState.Starting or DireWolfState.Stopping) await StopAsync().ConfigureAwait(false);
    }
}
