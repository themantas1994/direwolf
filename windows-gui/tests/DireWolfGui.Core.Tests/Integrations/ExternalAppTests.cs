using DireWolfGui.Core.Integrations;
using DireWolfGui.Core.Tests.Config;

namespace DireWolfGui.Core.Tests.Integrations;

public class ExternalAppTests
{
    [Fact]
    public void Catalog_has_the_expected_templates()
    {
        string[] ids = ["aprsisce32", "yaac", "xastir", "uiview32", "bpq32", "winlink-express", "outpost", "pinpoint", "sartrack", "packet-commander", "generic-agwpe", "generic-kiss"];
        Assert.Equal(ids, ExternalAppCatalog.All.Select(t => t.Id));
        Assert.Equal(ids.Length, ExternalAppCatalog.All.Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public void Guidance_is_conservative()
    {
        foreach (var t in ExternalAppCatalog.All)
        {
            Assert.Contains(t.PreferredProtocol, t.SupportedProtocols);
            string all = string.Join(" ", t.SetupSteps);
            Assert.True(all.Contains("127.0.0.1 port 8000") || all.Contains("127.0.0.1 port 8001"), t.Id);
            Assert.Contains("documentation", all);
            // No invented menu paths.
            Assert.DoesNotContain(" > ", all);
            Assert.DoesNotContain("->", all);
            var p = t.CreateProfile();
            Assert.Equal("127.0.0.1", p.Host);
            Assert.Equal(t.PreferredProtocol == TncProtocol.KissTcp ? 8001 : 8000, p.Port);
        }
        // Dire Wolf is only the TNC: BBS / Winlink functions belong to the application.
        Assert.Contains("not Dire Wolf", ExternalAppCatalog.Find("bpq32")!.Purpose);
        Assert.Contains("not Dire Wolf", ExternalAppCatalog.Find("WINLINK-EXPRESS")!.Purpose);
        Assert.Null(ExternalAppCatalog.Find("nope"));
    }

    [Fact]
    public void Connection_summary()
    {
        Assert.Equal("AGWPE (AGW TCP) at 127.0.0.1:8000", new ExternalAppProfile().ConnectionSummary);
        Assert.Equal("Serial KISS on COM5", new ExternalAppProfile { Protocol = TncProtocol.SerialKiss, SerialPort = "COM5" }.ConnectionSummary);
    }

    [Fact]
    public void Launcher_returns_errors_instead_of_throwing()
    {
        Assert.False(ExternalAppLauncher.Launch(new ExternalAppProfile { Name = "x" }).Success);
        var r = ExternalAppLauncher.Launch(new ExternalAppProfile { Name = "x", ExePath = "/no/such/program" });
        Assert.False(r.Success);
        Assert.Contains("not found", r.Error);
        using var t = new TempDir();
        Assert.False(ExternalAppLauncher.Launch(new ExternalAppProfile { Name = "x", ExePath = Environment.ProcessPath, WorkingDirectory = t.File("missing") }).Success);
        Assert.False(ExternalAppLauncher.OpenFolder(t.File("missing")).Success);
    }

    [Fact]
    public void Launcher_passes_arguments_verbatim()
    {
        if (OperatingSystem.IsWindows()) return; // uses /bin/sh
        using var t = new TempDir();
        string outFile = t.File("args.txt");
        var r = ExternalAppLauncher.Launch(new ExternalAppProfile
        {
            Name = "sh", ExePath = "/bin/sh", WorkingDirectory = t.Path,
            Arguments = ["-c", "printf '%s|' \"$@\" > args.txt", "sh", "a b", "c\"d", ""],
        });
        Assert.True(r.Success, r.Error);
        for (int i = 0; i < 100 && !(File.Exists(outFile) && File.ReadAllText(outFile).EndsWith("||")); i++) Thread.Sleep(50);
        Assert.Equal("a b|c\"d||", File.ReadAllText(outFile));
    }
}
