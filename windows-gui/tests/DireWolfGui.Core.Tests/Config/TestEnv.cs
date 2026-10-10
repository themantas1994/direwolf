namespace DireWolfGui.Core.Tests.Config;

/// <summary>Locations shared by the configuration tests.</summary>
internal static class TestEnv
{
    private static readonly Lazy<string> Root = new(() =>
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "config.c"))) return d.FullName;
        throw new InvalidOperationException("Repository root (with src/config.c) not found above " + AppContext.BaseDirectory);
    });

    public static string RepoRoot => Root.Value;

    /// <summary>DIREWOLF_EXE, else &lt;repo&gt;/build/src/direwolf(.exe).</summary>
    public static string DireWolfExe
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DIREWOLF_EXE");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            string p = Path.Combine(RepoRoot, "build", "src", OperatingSystem.IsWindows() ? "direwolf.exe" : "direwolf");
            if (!File.Exists(p) && OperatingSystem.IsWindows()) p = Path.Combine(RepoRoot, "build", "src", "Release", "direwolf.exe");
            return p;
        }
    }

    public static bool HaveDireWolf => File.Exists(DireWolfExe);

    /// <summary>All configuration files in the repository used for round-trip tests.</summary>
    public static IEnumerable<string> SampleConfigs()
    {
        foreach (var dir in new[] { "conf", "test" })
        {
            string d = Path.Combine(RepoRoot, dir);
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                if (dir == "conf" || f.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                    yield return f;
        }
    }
}

/// <summary>Fact that is skipped, with a clear reason, when no direwolf executable is available.</summary>
public sealed class DireWolfFactAttribute : FactAttribute
{
    public DireWolfFactAttribute()
    {
        if (!TestEnv.HaveDireWolf)
            Skip = $"Integration test skipped: direwolf executable not found at \"{TestEnv.DireWolfExe}\". Build Dire Wolf or set DIREWOLF_EXE.";
    }
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dwgui-test-" + Guid.NewGuid().ToString("N")[..10]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}
