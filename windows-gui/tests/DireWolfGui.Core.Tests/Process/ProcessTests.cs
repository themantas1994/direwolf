using DireWolfGui.Core.Process;

namespace DireWolfGui.Core.Tests.Process;

public class LaunchOptionsTests
{
    [Fact]
    public void BuildsArgumentsInOrder()
    {
        var o = new DireWolfLaunchOptions
        {
            ExecutablePath = "direwolf", ConfigPath = "C:\\My Files\\dw.conf", AudioStatsIntervalSeconds = 30, LogDirectory = "logs dir",
            ExtraArguments = new[] { "-q", "d" },
        };
        Assert.Equal(new[] { "-t", "0", "-c", "C:\\My Files\\dw.conf", "-a", "30", "-l", "logs dir", "-q", "d" }, DireWolfLaunchOptions.BuildArgumentList(o));
        var s = new DireWolfLaunchOptions { ExecutablePath = "direwolf", ConfigPath = "x.conf", StandardInputAudio = true, LogFile = "f.log" };
        Assert.Equal(new[] { "-t", "0", "-c", "x.conf", "-L", "f.log", "-" }, DireWolfLaunchOptions.BuildArgumentList(s));
    }

    [Fact]
    public void RefusesCalibrationAndBadCombinations()
    {
        Assert.Throws<ArgumentException>(() => DireWolfLaunchOptions.BuildArgumentList(new DireWolfLaunchOptions { ExecutablePath = "d", ConfigPath = "c", ExtraArguments = new[] { "-x" } }));
        Assert.Throws<ArgumentException>(() => DireWolfLaunchOptions.BuildArgumentList(new DireWolfLaunchOptions { ExecutablePath = "d", ConfigPath = "c", ExtraArguments = new[] { "-xa" } }));
        Assert.Throws<ArgumentException>(() => DireWolfLaunchOptions.BuildArgumentList(new DireWolfLaunchOptions { ExecutablePath = "d", ConfigPath = "" }));
        Assert.Throws<ArgumentException>(() => DireWolfLaunchOptions.BuildArgumentList(new DireWolfLaunchOptions { ExecutablePath = "d", ConfigPath = "c", LogDirectory = "a", LogFile = "b" }));
        var exe = Path.Combine(Path.GetTempPath(), "dw", "direwolf");
        Assert.Equal(Path.GetDirectoryName(exe), new DireWolfLaunchOptions { ExecutablePath = exe, ConfigPath = "c" }.EffectiveWorkingDirectory);
    }
}

public class ProbeTests
{
    [Fact]
    public void ParsesBanners()
    {
        var r = DireWolfProbe.ParseBanner("dw", new[] { "Dire Wolf Release 1.8.2, November 2025", "Includes optional support for:  cm108-ptt hamlib dns-sd" })!;
        Assert.Equal((1, 8, "2", false), (r.Major!.Value, r.Minor!.Value, r.Patch, r.IsDevelopment));
        Assert.Equal(new[] { "cm108-ptt", "hamlib", "dns-sd" }, r.Features);
        Assert.Equal("1.8.2", r.DisplayVersion);
        var old = DireWolfProbe.ParseBanner("dw", new[] { "Dire Wolf version 1.7" })!;
        Assert.Equal((1, 7, null as string), (old.Major!.Value, old.Minor!.Value, old.Patch));
        var dev = DireWolfProbe.ParseBanner("dw", new[] { "Dire Wolf DEVELOPMENT version 1.8 E (Oct  1 2025)" })!;
        Assert.True(dev.IsDevelopment);
        Assert.Equal(8, dev.Minor);
        Assert.Null(DireWolfProbe.ParseBanner("dw", new[] { "hello" }));
    }

    [Fact]
    public void CandidatesIncludeGuiDirectory()
    {
        var c = DireWolfProbe.CandidateExecutablePaths("/gui");
        Assert.Equal(Path.Combine("/gui", DireWolfProbe.ExecutableName), c[0]);
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void ExplainsKnownFailures()
    {
        Assert.Contains("configuration file could not be opened",
            DireWolfDiagnostics.Diagnose(new[] { "Dire Wolf Release 1.8.2", "ERROR - Could not open configuration file x.conf in cwd or homedir." }, 1).Summary);
        Assert.Contains("audio device could not be opened",
            DireWolfDiagnostics.Diagnose(new[] { "Could not open audio device plughw:1,0 for input", "No such file or directory" }, 1).Summary);
        var cfg = DireWolfDiagnostics.Diagnose(new[] { "Line 3: Invalid channel number." }, 1);
        Assert.StartsWith("Configuration problem", cfg.Summary);
        Assert.Single(cfg.ErrorLines);
        Assert.Contains("code 3", DireWolfDiagnostics.Diagnose(new[] { "all fine" }, 3).Summary);
        Assert.DoesNotContain("12345", DireWolfDiagnostics.Diagnose(new[] { "ERROR bad IGLOGIN N0CALL 12345" }, 1).Summary);
    }
}

public class ControllerTests
{
    [Fact]
    public async Task MissingExecutableIsReported()
    {
        var c = new DireWolfProcessController();
        var states = new List<DireWolfState>();
        c.StateChanged += (_, e) => states.Add(e.NewState);
        var ex = await Assert.ThrowsAsync<DireWolfStartException>(() =>
            c.StartAsync(new DireWolfLaunchOptions { ExecutablePath = "/nonexistent/direwolf", ConfigPath = "x.conf" }));
        Assert.Contains("not found", ex.Message);
        Assert.Equal(DireWolfState.Failed, c.State);
        Assert.Contains(DireWolfState.Failed, states);
        Assert.Equal(StopOutcome.NotRunning, (await c.StopAsync()).Outcome);
    }

    [Fact]
    public async Task KillsWhenInterruptIsIgnored()
    {
        if (OperatingSystem.IsWindows()) return;   // uses a POSIX shell script as a stand-in process
        string dir = Path.Combine(Path.GetTempPath(), "dwctl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "fake-direwolf");
        File.WriteAllText(script, "#!/bin/sh\ntrap '' INT\necho \"Dire Wolf Release 9.9.9\"\nsleep 30\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await using var c = new DireWolfProcessController();
            var lines = new List<string>();
            var gotLine = new TaskCompletionSource();
            c.OutputLine += (_, e) => { lock (lines) lines.Add(e.Line); gotLine.TrySetResult(); };
            var opts = new DireWolfLaunchOptions { ExecutablePath = script, ConfigPath = "x.conf" };
            // Executing a script just written can fail with ETXTBSY ("Text file busy") on Linux while
            // another test's process start briefly holds a copy of the write handle; retry a few times.
            for (int attempt = 1; ; attempt++)
            {
                try { await c.StartAsync(opts); break; }
                catch (DireWolfStartException e) when (attempt < 10 && e.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 26 })
                {
                    await Task.Delay(100);
                }
            }
            Assert.Equal(DireWolfState.Running, c.State);
            await gotLine.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Dire Wolf Release 9.9.9", lines);

            // Duplicate instance is refused, both on this controller and on another one with the same config.
            await Assert.ThrowsAsync<InvalidOperationException>(() => c.StartAsync(opts));
            var other = new DireWolfProcessController();
            await Assert.ThrowsAsync<DireWolfStartException>(() => other.StartAsync(opts));

            Assert.NotNull(c.SampleResources());
            int pid = c.ProcessId!.Value;
            var r = await c.StopAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(StopOutcome.Killed, r.Outcome);
            Assert.True(r.PttMayBeKeyed);
            Assert.Equal(DireWolfState.Stopped, c.State);
            Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
            Assert.DoesNotContain(DireWolfProcessController.ManagedConfigFiles, f => f.StartsWith(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
