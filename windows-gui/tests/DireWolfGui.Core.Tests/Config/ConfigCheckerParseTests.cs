using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Tests.Config;

public class ConfigCheckerParseTests
{
    private const string GoodOutput = """
        Dire Wolf Release 1.8.2, November 2025
        Includes optional support for:  cm108-ptt

        Why are you running this as root user?.
        Dire Wolf requires only privileges available to ordinary users.
        Running this as root is an unnecessary security risk.

        Reading config file /x/good.conf
        Channel 1: EXPERIMENTAL WAPR modem, profile H150.  Not compatible with AX.25 / APRS radios.
        check-config: version 1.8.2
        check-config: features wapr fx25 il2p cm108
        check-config: adevice 0 in "stdin" out "null" rate 44100 channels 2
        check-config: channel 0 radio mycall N0CALL-1 modem AFSK baud 1200 mark 1200 space 2200 profiles "" ptt SERIAL fx25tx 16
        check-config: channel 1 radio mycall N0CALL-2 modem WAPR baud 150 mark 0 space 0 profiles "" ptt NONE wapr H150 airtime 10
        check-config: channel 9 igate mycall N0CALL-1
        check-config: agwport 8000
        check-config: tcpbind local
        check-config: kissport 8001 chan -1
        check-config: serialkiss "COM3" speed 9600
        check-config: digipeat 0 0
        check-config: regen 0 1
        check-config: cdigipeat 0 0
        check-config: igate server "noam.aprs2.net" port 14580 login N0CALL txchan -1
        check-config: beacon POSITION sendto XMIT chan 0 line 16
        check-config: waprgate 1 0 types 0x5
        check-config: waprgate 1 -1 types 0xff
        check-config: result 0 diagnostics
        """;

    [Fact]
    public void Good_output_gives_typed_summary()
    {
        var r = ConfigChecker.ParseOutput(GoodOutput, 0);
        Assert.Equal(CheckConfigStatus.Ok, r.Status);
        Assert.Empty(r.Diagnostics);
        Assert.Single(r.Notes);
        var s = r.Summary!;
        Assert.Equal("1.8.2", s.Version);
        Assert.True(s.HasFeature("wapr"));
        Assert.Equal(new CheckAudioDevice(0, "stdin", "null", 44100, 2), s.AudioDevices.Single());
        var c0 = s.Channel(0)!;
        Assert.Equal(("N0CALL-1", "AFSK", 1200, 2200, "SERIAL", 16), (c0.MyCall, c0.Modem, c0.Baud, c0.Space, c0.Ptt, c0.Fx25Tx!.Value));
        var c1 = s.Channel(1)!;
        Assert.True(c1.IsWapr);
        Assert.Equal(("H150", 10.0), (c1.WaprProfile, c1.WaprAirtimePercent!.Value));
        Assert.Equal(ChannelMedium.IGate, s.Channel(9)!.Medium);
        Assert.Equal(8000, s.AgwPort);
        Assert.True(s.BindLocalOnly);
        Assert.True(s.TcpBindReported);
        Assert.Equal(new CheckKissPort(8001, -1), s.KissPorts.Single());
        Assert.Equal(new CheckSerialKiss("COM3", 9600), s.SerialKiss);
        Assert.Equal(new CheckChannelPair(0, 0), s.Digipeat.Single());
        Assert.Equal(new CheckChannelPair(0, 1), s.Regen.Single());
        Assert.Equal(new CheckChannelPair(0, 0), s.CDigipeat.Single());
        Assert.Equal(new CheckIGate("noam.aprs2.net", 14580, "N0CALL", -1), s.IGate);
        Assert.Equal(new CheckBeacon("POSITION", "XMIT", 0, 16), s.Beacons.Single());
        Assert.Equal(5, s.WaprGates[0].Types);
        Assert.True(s.WaprGates[1].ToInternet);
        Assert.Equal(0, s.DiagnosticCount);
        Assert.Empty(s.UnknownLines);
    }

    [Fact]
    public void Tcpbind_any_and_old_builds()
    {
        var any = ConfigChecker.ParseSummary(["check-config: tcpbind any"]);
        Assert.False(any.BindLocalOnly);
        Assert.True(any.TcpBindReported);
        Assert.False(ConfigChecker.ParseSummary(["check-config: agwport 8000"]).TcpBindReported);
    }

    [Fact]
    public void Empty_mycall_is_parsed()
    {
        var s = ConfigChecker.ParseSummary(["check-config: channel 0 radio mycall  modem AFSK baud 1200 mark 1200 space 2200 profiles \"\" ptt NONE"]);
        Assert.Equal("", s.Channel(0)!.MyCall);
    }

    [Fact]
    public void Diagnostics_have_line_numbers()
    {
        const string output = """
            Reading config file /x/bad.conf
            Config file: Unrecognized command 'BOGUS' on line 4.
            Line 5: MODEM WAPR needs a profile name: F600, H150 or R25.
            Line 7: Keeping with tradition, going back to the 1980s, TXDELAY is in 10 millisecond units.
            Line 7: The value 150 would be 1.500 seconds which seems rather excessive.  Are you sure you want that?
            Read the Dire Wolf User Guide, "Radio Channel - Transmit Timing"
            section, to understand what this means.
            Config file line 9: Missing RTS or DTR after PTT device name.
            Config file: Beaconing should be configured for channel 0 when digipeating is enabled.
            check-config: version 1.8.2
            check-config: result 5 diagnostics
            """;
        var r = ConfigChecker.ParseOutput(output, 1);
        Assert.Equal(CheckConfigStatus.Diagnostics, r.Status);
        Assert.Equal([4, 5, 7, 9, null], r.Diagnostics.Select(d => d.Line));
        Assert.Contains("section, to understand", r.Diagnostics[2].Message);
        Assert.Equal(DiagnosticSeverity.Warning, r.Diagnostics[2].Severity);
        Assert.Equal(DiagnosticSeverity.Error, r.Diagnostics[0].Severity);
        Assert.Equal(DiagnosticSeverity.Warning, r.Diagnostics[4].Severity);
        Assert.All(r.Diagnostics, d => Assert.Equal(DiagnosticSource.DireWolf, d.Source));
        Assert.Equal(5, r.Summary!.DiagnosticCount);
    }

    [Fact]
    public void Unsupported_build_is_detected()
    {
        const string output = """
            Dire Wolf version 1.7
            direwolf: unrecognized option '--check-config'
            Usage: direwolf [options] [ - | stdin | UDP:nnnn ]
            Options:
                -c fname       Configuration file name.
            """;
        var r = ConfigChecker.ParseOutput(output, 1);
        Assert.Equal(CheckConfigStatus.Unsupported, r.Status);
        Assert.False(r.IsSupported);
        Assert.Contains("unavailable for this Dire Wolf build", r.Message);
        Assert.Null(r.Summary);
    }

    [Fact]
    public void Fatal_error_without_result_is_failed()
    {
        var r = ConfigChecker.ParseOutput("Reading config file x\nConfig file: Missing name of audio device for ADEVICE command on line 1.\n", 1);
        Assert.Equal(CheckConfigStatus.Failed, r.Status);
        Assert.Equal(1, r.Diagnostics.Single().Line);
    }

    [Fact]
    public void Passcodes_are_redacted()
    {
        Assert.Equal("IGLOGIN K1ABC *****", ConfigChecker.Redact("IGLOGIN K1ABC 23456"));
        Assert.Equal("login with pass *****", ConfigChecker.Redact("login with pass 23456"));
        Assert.Equal("x ***** y 123456789", ConfigChecker.Redact("x 23456 y 123456789", ["23456"]));
        Assert.Equal("txchan -1", ConfigChecker.Redact("txchan -1", ["-1"]));
        var r = ConfigChecker.ParseOutput("Reading config file x\nLine 3: something IGLOGIN N0CALL 98765 odd\ncheck-config: result 1 diagnostics\n", 1, ["98765"]);
        Assert.DoesNotContain("98765", r.RawOutput);
        Assert.DoesNotContain("98765", r.Diagnostics[0].Message);
    }

    [Fact]
    public void Secrets_are_found_in_document()
        => Assert.Equal(["4321"], ConfigChecker.FindSecrets(ConfigDocument.Parse("IGSERVER x\nIGLOGIN K1ABC 4321\n")));

    [Fact]
    public async Task Missing_executable_is_reported_not_thrown()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.File("a.conf"), "MYCALL K1ABC\n");
        var r = await ConfigChecker.CheckFileAsync(tmp.File("no-such-direwolf"), tmp.File("a.conf"));
        Assert.Equal(CheckConfigStatus.Failed, r.Status);
        Assert.Contains("Could not start", r.Message);
    }
}
