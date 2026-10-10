using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DireWolfGui.Core.Tests.Integration;

/// <summary>All tests that run the real direwolf share ports (and its default KISS port), so they never run in parallel.</summary>
[CollectionDefinition("direwolf", DisableParallelization = true)]
public sealed class DireWolfCollection { }

/// <summary>[Fact] that is skipped with a clear reason when no direwolf build is available.</summary>
public sealed class DireWolfFactAttribute : FactAttribute
{
    public DireWolfFactAttribute()
    {
        if (DireWolfTestEnvironment.SkipReason is string reason) Skip = reason;
    }
}

/// <summary>
/// [Fact] for tests that run direwolf with its audio open.  Dire Wolf for Windows needs a real
/// output device even when receive audio comes from stdin; machines without one (e.g. hosted
/// CI runners) skip these tests with that reason.  On Linux the ALSA "null" device is used.
/// </summary>
public sealed class DireWolfAudioFactAttribute : FactAttribute
{
    public DireWolfAudioFactAttribute()
    {
        if ((DireWolfTestEnvironment.SkipReason ?? DireWolfTestEnvironment.AudioSkipReason) is string reason) Skip = reason;
    }
}

/// <summary>
/// Locates a real direwolf build (env DIREWOLF_EXE, else build/src/direwolf in the repository root or an
/// ancestor), creates working directories with the data files, and makes audio with gen_packets.
/// </summary>
public static class DireWolfTestEnvironment
{
    public static readonly string? Exe = FindExe();
    public static readonly string? DataDir = FindUp(Path.Combine("data", "tocalls.yaml")) is string t ? Path.GetDirectoryName(t) : null;
    public static readonly string? GenPackets = Exe == null ? null : Path.Combine(Path.GetDirectoryName(Exe)!, OperatingSystem.IsWindows() ? "gen_packets.exe" : "gen_packets");

    public static string? SkipReason =>
        Exe == null ? "Real direwolf not found: set DIREWOLF_EXE or build it at <repo>/build/src/direwolf (cmake .. && make)." :
        GenPackets == null || !File.Exists(GenPackets) ? $"gen_packets not found next to {Exe}; it is needed to make test audio." :
        DataDir == null ? "Repository data directory (data/tocalls.yaml) not found." : null;

    public static string? AudioSkipReason =>
        OperatingSystem.IsWindows() && WaveOutDeviceCount() == 0
            ? "No audio output device on this computer: Dire Wolf for Windows needs one to start, even with audio input from stdin."
            : null;

    /// <summary>Transmit audio device in test configurations: discarded on Linux, the first device on Windows.</summary>
    public static string OutputDevice => OperatingSystem.IsWindows() ? "0" : "null";

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    private static uint WaveOutDeviceCount()
    {
        try { return waveOutGetNumDevs(); } catch (Exception) { return 0; }
    }

    private static string? FindExe()
    {
        var env = Environment.GetEnvironmentVariable("DIREWOLF_EXE");
        if (!string.IsNullOrWhiteSpace(env)) return File.Exists(env) ? Path.GetFullPath(env) : null;
        return FindUp(Path.Combine("build", "src", OperatingSystem.IsWindows() ? "direwolf.exe" : "direwolf"));
    }

    /// <summary>Looks for <paramref name="relative"/> in the test directory and each ancestor (works from git worktrees too).</summary>
    private static string? FindUp(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, relative);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static string CreateWorkDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dwgui-it-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "tocalls.yaml", "symbols-new.txt", "symbolsX.txt" })
            File.Copy(Path.Combine(DataDir!, f), Path.Combine(dir, f));
        return dir;
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>Hardware-free configuration: audio from stdin, transmit audio discarded.</summary>
    public static string WriteConfig(string dir, int agwPort, int kissPort, string myCall = "N0TEST")
    {
        string path = Path.Combine(dir, "test.conf");
        File.WriteAllText(path, $"""
            ADEVICE stdin {OutputDevice}
            CHANNEL 0
            MYCALL {myCall}
            MODEM 1200
            AGWPORT {agwPort}
            KISSPORT 0
            KISSPORT {kissPort}
            """.Replace("\r\n", "\n") + "\n");
        return path;
    }

    /// <summary>Raw S16LE 44100 Hz mono audio of the packets (one gen_packets run each, so no stray newlines), with silence after each.</summary>
    public static byte[] GenerateAudio(string dir, params string[] packets)
    {
        var all = new MemoryStream();
        var silence = new byte[44100 * 2 / 2];
        for (int i = 0; i < packets.Length; i++)
        {
            string msg = Path.Combine(dir, $"msg{i}.txt");
            string wav = Path.Combine(dir, $"msg{i}.wav");
            File.WriteAllBytes(msg, Encoding.UTF8.GetBytes(packets[i]));   // no trailing newline
            var psi = new ProcessStartInfo(GenPackets!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
            foreach (var a in new[] { "-r", "44100", "-o", wav, msg }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("gen_packets failed for " + packets[i]);
            var bytes = File.ReadAllBytes(wav);
            all.Write(bytes, 44, bytes.Length - 44);   // gen_packets writes a canonical 44 byte header
            all.Write(silence);
        }
        all.Write(silence);
        return all.ToArray();
    }

    public static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    public static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
