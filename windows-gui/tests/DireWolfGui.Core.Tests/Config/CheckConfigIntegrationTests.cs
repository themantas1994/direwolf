using DireWolfGui.Core.Config;
using DireWolfGui.Core.Wapr;

namespace DireWolfGui.Core.Tests.Config;

/// <summary>Runs the real direwolf --check-config (skipped when no executable is found).</summary>
public class CheckConfigIntegrationTests
{
    private static Task<CheckConfigResult> Check(ConfigDocument doc) => ConfigChecker.CheckDocumentAsync(TestEnv.DireWolfExe, doc);

    private static Task<CheckConfigResult> Check(string text) => Check(ConfigDocument.Parse(text.Replace("\r\n", "\n")));

    private static void AssertClean(CheckConfigResult r)
    {
        Assert.True(r.Status == CheckConfigStatus.Ok, $"{r.Status}: {r.Message}\n{string.Join("\n", r.Diagnostics)}\n{r.RawOutput}");
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity >= DiagnosticSeverity.Warning);
    }

    private static void AssertZero(CheckConfigResult r)
    {
        AssertClean(r);
        Assert.Equal(0, r.Summary!.DiagnosticCount);
        Assert.Equal(0, r.ExitCode);
        Assert.Empty(r.Diagnostics);
    }

    [DireWolfFact]
    public async Task Repository_good_fixture_is_clean_and_passcode_never_appears()
    {
        var r = await ConfigChecker.CheckFileAsync(TestEnv.DireWolfExe, Path.Combine(TestEnv.RepoRoot, "test", "check-config", "good.conf"));
        AssertZero(r);
        var s = r.Summary!;
        Assert.True(WaprSupport.IsSupported(s));
        Assert.Equal("WAPR", s.Channel(1)!.Modem);
        Assert.Equal("H150", s.Channel(1)!.WaprProfile);
        Assert.Equal(10, s.Channel(1)!.WaprAirtimePercent);
        Assert.Equal(8000, s.AgwPort);
        Assert.Equal(new CheckIGate("noam.aprs2.net", 14580, "N0CALL", -1), s.IGate);
        Assert.Equal(new CheckWaprGate(1, 0, 0x5), s.WaprGates.Single());
        Assert.Equal(18, s.Beacons.Single().Line);
        Assert.DoesNotContain("12345", r.RawOutput);
        Assert.Contains("EXPERIMENTAL WAPR", r.Notes.Single());
    }

    [DireWolfFact]
    public async Task Bad_config_reports_diagnostics_with_line_numbers()
    {
        var r = await Check("ADEVICE stdin null\nMYCALL K1ABC\n# comment\nBOGUS 1\nMODEM WAPR X99\nTXDELAY 300\n");
        Assert.Equal(CheckConfigStatus.Diagnostics, r.Status);
        Assert.Equal(1, r.ExitCode);
        Assert.Contains(r.Diagnostics, d => d.Line == 4 && d.Message.Contains("BOGUS"));
        Assert.Contains(r.Diagnostics, d => d.Line == 5 && d.Message.Contains("WAPR needs a profile"));
        Assert.Contains(r.Diagnostics, d => d.Line == 6);
        Assert.Equal(3, r.Summary!.DiagnosticCount);
        // The GUI validator agrees on the lines.
        var gui = ConfigValidator.Validate(ConfigDocument.Parse("ADEVICE stdin null\nMYCALL K1ABC\n# comment\nBOGUS 1\nMODEM WAPR X99\nTXDELAY 300\n"));
        Assert.Equal([4, 5, 6], gui.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Line!.Value));
    }

    [DireWolfFact]
    public async Task Tcpbind_is_reported()
    {
        var local = await Check("ADEVICE stdin null\nMYCALL K1ABC\nTCPBIND LOCAL\n");
        AssertZero(local);
        Assert.True(local.Summary!.TcpBindReported);
        Assert.True(local.Summary.BindLocalOnly);
        var any = await Check("ADEVICE stdin null\nMYCALL K1ABC\nTCPBIND ANY\n");
        Assert.False(any.Summary!.BindLocalOnly);
        var bad = await Check("ADEVICE stdin null\nMYCALL K1ABC\nTCPBIND everywhere\n");
        Assert.Contains(bad.Diagnostics, d => d.Line == 3 && d.Message.Contains("TCPBIND"));
    }

    public static TheoryData<int, string?> ModemChoices() => new()
    {
        { 300, null }, { 1200, null }, { 2400, null }, { 4800, null }, { 9600, null }, { 1200, "F600" }, { 1200, "H150" }, { 1200, "R25" },
    };

    [DireWolfTheory]
    [MemberData(nameof(ModemChoices))]
    public async Task Wizard_templates_pass_the_real_parser(int speed, string? wapr)
    {
        var o = ConfigTemplatesTests.Basic(speed, wapr);
        o.AudioDevices[0].Input = "stdin";
        o.AudioDevices[0].Output = "null";
        if (wapr != null) o.Channels[0].WaprAirtimePercent = 20;
        var doc = ConfigDocument.Parse(ConfigTemplates.NewStation(o));
        var r = await Check(doc);
        AssertZero(r);
        var ch = r.Summary!.Channel(0)!;
        Assert.Equal("K1ABC-7", ch.MyCall);
        Assert.Equal("SERIAL", ch.Ptt);
        Assert.True(r.Summary.BindLocalOnly);
        if (wapr != null) Assert.Equal((wapr, 20.0), (ch.WaprProfile, ch.WaprAirtimePercent!.Value));
        else Assert.Equal(speed, ch.Baud);
    }

    [DireWolfFact]
    public async Task Template_with_all_options_activated_passes()
    {
        var o = ConfigTemplatesTests.Basic();
        o.AudioDevices = [new AudioDeviceOption { Input = "stdin", Output = "null", Stereo = true }];
        // (PTT CM108 is not used here: with the "null" audio device Dire Wolf cannot find the adapter and says so.)
        o.Channels.Add(new ChannelOption { Channel = 1, WaprProfile = "H150", Ptt = PttMethod.SerialDtr, PttSerialPort = "COM4", PttInvert = true });
        o.IncludeBeacon = o.IncludeDigipeater = o.IncludeIGate = o.ActivateOptionalServices = true;
        o.Latitude = 42.6; o.Longitude = -71.3; o.BeaconComment = "test station";
        o.IGatePasscode = "12345";
        o.AllowRemoteClients = true;
        var r = await Check(ConfigDocument.Parse(ConfigTemplates.NewStation(o)));
        AssertZero(r);
        Assert.Single(r.Summary!.Digipeat);
        Assert.Equal("SERIAL", r.Summary.Channel(1)!.Ptt);
        Assert.False(r.Summary.BindLocalOnly);
        Assert.NotNull(r.Summary.IGate);
        Assert.DoesNotContain("12345", r.RawOutput);
    }

    [DireWolfFact]
    public async Task Commented_out_template_passes_and_document_edits_still_pass()
    {
        var o = ConfigTemplatesTests.Basic();
        o.AudioDevices = [new AudioDeviceOption { Input = "stdin", Output = "null" }];
        o.IncludeBeacon = o.IncludeDigipeater = o.IncludeIGate = true;
        var doc = ConfigDocument.Parse(ConfigTemplates.NewStation(o));
        AssertZero(await Check(doc));

        doc.SetDirective("ACHANNELS", ["2"], 0);
        doc.SetDirective("MODEM", ["WAPR", "R25"], 1);
        doc.SetDirective("MYCALL", ["K1ABC-9"], 1);
        doc.SetDirective("TXDELAY", ["40"], 0);
        doc.SetDirective("WAPRGATE", "1", "0", "POS,MSG");
        doc.SetDirective("KISSPORT", "8010");
        doc.DisableDirective("AGWPORT");
        var r = await Check(doc);
        AssertZero(r); // a valid TXDELAY is not a diagnostic (fixed in this fork's --check-config count)
        Assert.Equal("R25", r.Summary!.Channel(1)!.WaprProfile);
        Assert.Equal("K1ABC-9", r.Summary.Channel(1)!.MyCall);
        Assert.Equal(new CheckWaprGate(1, 0, 0x5), r.Summary.WaprGates.Single());
        Assert.Contains(r.Summary.KissPorts, k => k.Port == 8010);
        Assert.Equal(8000, r.Summary.AgwPort); // disabled line: Dire Wolf's default applies again
        Assert.DoesNotContain(ConfigValidator.Validate(doc), d => d.Severity == DiagnosticSeverity.Error);
    }

    [DireWolfFact]
    public async Task Cm108_problems_from_the_real_parser_are_passed_on()
    {
        var o = ConfigTemplatesTests.Basic();
        o.AudioDevices = [new AudioDeviceOption { Input = "stdin", Output = "null" }];
        o.Channels[0].Ptt = PttMethod.Cm108;
        var r = await Check(ConfigDocument.Parse(ConfigTemplates.NewStation(o)));
        Assert.Equal(CheckConfigStatus.Diagnostics, r.Status);
        Assert.Contains(r.Diagnostics, d => d.Source == DiagnosticSource.DireWolf && d.Message.Contains("HID", StringComparison.OrdinalIgnoreCase));
    }

    [DireWolfFact]
    public async Task Repository_bad_fixture()
    {
        var r = await ConfigChecker.CheckFileAsync(TestEnv.DireWolfExe, Path.Combine(TestEnv.RepoRoot, "test", "check-config", "bad.conf"));
        Assert.Equal(CheckConfigStatus.Diagnostics, r.Status);
        Assert.Equal([4, 5, 6], r.Diagnostics.Select(d => d.Line!.Value));
    }
}

public sealed class DireWolfTheoryAttribute : TheoryAttribute
{
    public DireWolfTheoryAttribute()
    {
        if (!TestEnv.HaveDireWolf)
            Skip = $"Integration test skipped: direwolf executable not found at \"{TestEnv.DireWolfExe}\". Build Dire Wolf or set DIREWOLF_EXE.";
    }
}
