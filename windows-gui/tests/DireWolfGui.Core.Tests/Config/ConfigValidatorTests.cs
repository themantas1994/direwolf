using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Tests.Config;

public class ConfigValidatorTests
{
    private static IReadOnlyList<ConfigDiagnostic> V(string text, CheckConfigSummary? s = null) => ConfigValidator.Validate(ConfigDocument.Parse(text.Replace("\r\n", "\n")), s);

    private static ConfigDiagnostic One(IReadOnlyList<ConfigDiagnostic> d, string code) =>
        Assert.Single(d, x => x.Code == code);

    private const string Good = "ADEVICE a b\nACHANNELS 2\nCHANNEL 0\nMYCALL K1ABC-1\nMODEM 1200\nCHANNEL 1\nMYCALL K1ABC-2\nMODEM WAPR H150\nTCPBIND LOCAL\n";

    [Fact]
    public void Good_config_has_no_errors_or_warnings()
    {
        var d = V(Good);
        Assert.DoesNotContain(d, x => x.Severity >= DiagnosticSeverity.Warning);
        Assert.Contains(d, x => x.Code == "wapr-experimental" && x.Line == 8);
        Assert.Contains(d, x => x.Code == "network-local");
    }

    [Fact]
    public void Unknown_directive_with_line_number() =>
        Assert.Equal(3, One(V("MYCALL K1ABC\n\nBOGUS 1\n"), "unknown-directive").Line);

    [Theory]
    [InlineData("MYCALL N0CALL\n", "mycall-placeholder")]
    [InlineData("MYCALL nocall-5\n", "mycall-placeholder")]
    [InlineData("MYCALL TOOLONGCALL\n", "mycall-invalid")]
    [InlineData("MYCALL K1ABC-16\n", "mycall-invalid")]
    [InlineData("MODEM 1200\n", "mycall-none")]
    public void Mycall_checks(string text, string code) => One(V(text), code);

    [Fact]
    public void Channel_needs_its_audio_device_and_stereo_in_order()
    {
        Assert.Equal(2, One(V("MYCALL K1ABC\nCHANNEL 1\n"), "channel-not-stereo").Line);
        Assert.Equal(2, One(V("MYCALL K1ABC\nCHANNEL 2\n"), "channel-no-device").Line);
        // ACHANNELS after the CHANNEL line is too late for the real parser.
        One(V("MYCALL K1ABC\nCHANNEL 1\nACHANNELS 2\n"), "channel-not-stereo");
        Assert.DoesNotContain(V("ADEVICE1 x\nMYCALL K1ABC\nCHANNEL 2\n"), x => x.Code.StartsWith("channel-"));
        One(V("MYCALL K1ABC\nCHANNEL 7\n"), "channel-range");
    }

    [Theory]
    [InlineData("MODEM WAPR\n")]
    [InlineData("MODEM WAPR X99\n")]
    public void Invalid_wapr_profile(string modem) => Assert.Equal(2, One(V("MYCALL K1ABC\n" + modem), "wapr-profile").Line);

    [Fact]
    public void Wapr_airtime_option_checked()
    {
        One(V("MYCALL K1ABC\nMODEM WAPR h150 AIRTIME=0\n"), "wapr-option");
        One(V("MYCALL K1ABC\nMODEM WAPR R25 SPEED=3\n"), "wapr-option");
        Assert.DoesNotContain(V("MYCALL K1ABC\nMODEM WAPR R25 AIRTIME=12.5\n"), x => x.Code == "wapr-option");
    }

    [Fact]
    public void Fec_on_wapr_channel()
    {
        var d = V("MYCALL K1ABC\nMODEM WAPR F600\nFX25TX 1\nIL2PTX 1\n");
        Assert.Equal([3, 4], d.Where(x => x.Code == "fec-on-wapr").Select(x => x.Line!.Value));
        Assert.DoesNotContain(V("MYCALL K1ABC\nMODEM 1200\nFX25TX 1\n"), x => x.Code == "fec-on-wapr");
    }

    [Fact]
    public void Wapr_unsupported_build()
    {
        var s = new CheckConfigSummary();
        s.Features.Add("fx25");
        One(V("MYCALL K1ABC\nMODEM WAPR H150\n", s), "wapr-unsupported");
    }

    [Theory]
    [InlineData("WAPRGATE 1 0 POS,MSG", null)]
    [InlineData("WAPRGATE 0 1", null)]
    [InlineData("WAPRGATE IS 1", "waprgate-from-is")]
    [InlineData("WAPRGATE 1", "waprgate-syntax")]
    [InlineData("WAPRGATE 1 0 POS,FOO", "waprgate-types")]
    [InlineData("WAPRGATE 1 1", "waprgate-channels")]
    [InlineData("WAPRGATE 1 4", "waprgate-channels")]
    [InlineData("WAPRGATE 0 0", "waprgate-channels")]
    [InlineData("WAPRGATE 1 IS", "waprgate-no-igate")]
    public void Waprgate_rules(string rule, string? code)
    {
        var d = V("ADEVICE a\nACHANNELS 2\nCHANNEL 0\nMYCALL K1ABC\nMODEM 1200\nCHANNEL 1\nMODEM WAPR H150\n" + rule + "\n");
        if (code == null) Assert.DoesNotContain(d, x => x.Code.StartsWith("waprgate-") && x.Severity == DiagnosticSeverity.Error);
        else Assert.Equal(8, One(d, code).Line);
    }

    [Fact]
    public void Waprgate_needs_a_wapr_side()
    {
        var d = V("ADEVICE a\nACHANNELS 2\nMYCALL K1ABC\nCHANNEL 1\nMODEM 9600\nWAPRGATE 0 1\n");
        One(d, "waprgate-no-wapr");
    }

    [Theory]
    [InlineData("TXDELAY 300", "range")]
    [InlineData("TXDELAY abc", "range")]
    [InlineData("TXDELAY 5", "range-unusual")]
    [InlineData("TXTAIL 2", "range-unusual")]
    [InlineData("DWAIT 256", "range")]
    [InlineData("SLOTTIME 50", "range")]
    [InlineData("SLOTTIME 4", "range")]
    [InlineData("PERSIST 251", "range")]
    [InlineData("PERSIST 4", "range")]
    public void Numeric_ranges(string line, string code) => Assert.Equal(2, One(V("MYCALL K1ABC\n" + line + "\n"), code).Line);

    [Theory]
    [InlineData("TXDELAY 30")]
    [InlineData("TXTAIL 10")]
    [InlineData("DWAIT 0")]
    [InlineData("SLOTTIME 10")]
    [InlineData("PERSIST 63")]
    public void Numeric_ok(string line) => Assert.DoesNotContain(V("MYCALL K1ABC\n" + line + "\n"), x => x.Code.StartsWith("range"));

    [Fact]
    public void Igate_login_and_server_pairing()
    {
        One(V("MYCALL K1ABC\nIGLOGIN K1ABC 123\n"), "iglogin-no-server");
        One(V("MYCALL K1ABC\nIGSERVER noam.aprs2.net\n"), "igserver-no-login");
        Assert.DoesNotContain(V("MYCALL K1ABC\nIGSERVER noam.aprs2.net\nIGLOGIN K1ABC 123\n"), x => x.Code.StartsWith("ig"));
    }

    [Fact]
    public void Digipeat_without_beacon_and_with_placeholder_call()
    {
        var d = V("MYCALL N0CALL\nDIGIPEAT 0 0 ^WIDE[3-7]-[1-7]$|^TEST$ ^WIDE[12]-[12]$\n");
        One(d, "digi-no-beacon");
        One(d, "digi-mycall");
        var ok = V("MYCALL K1ABC\nDIGIPEAT 0 0 a b\nPBEACON every=30 lat=1 long=2\n");
        Assert.DoesNotContain(ok, x => x.Code.StartsWith("digi-"));
    }

    [Fact]
    public void Network_exposure_unless_tcpbind_local()
    {
        var exposed = One(V("MYCALL K1ABC\nAGWPORT 8000\n"), "network-exposed");
        Assert.Equal(DiagnosticSeverity.Warning, exposed.Severity);
        Assert.Contains("reachable from other computers", exposed.Message);
        One(V("MYCALL K1ABC\n"), "network-exposed");            // default ports 8000 / 8001
        One(V("MYCALL K1ABC\nTCPBIND ANY\n"), "network-exposed");
        One(V("MYCALL K1ABC\nTCPBIND LOCAL\n"), "network-local");
        Assert.DoesNotContain(V("MYCALL K1ABC\nAGWPORT 0\nKISSPORT 0\n"), x => x.Code.StartsWith("network-"));
        One(V("MYCALL K1ABC\nTCPBIND loopback\n"), "tcpbind");
    }

    [Fact]
    public void Ports_duplicates_and_ranges()
    {
        Assert.Equal(3, One(V("MYCALL K1ABC\nAGWPORT 8001\nKISSPORT 8001\n"), "port-duplicate").Line);
        One(V("MYCALL K1ABC\nAGWPORT 80\n"), "port-range");
    }

    [Fact]
    public void Overrides_and_inline_comments()
    {
        var d = V("MYCALL K1ABC\nCHANNEL 0\nMODEM 1200\nMODEM 9600 # fast\n");
        Assert.Equal(4, One(d, "overridden").Line);
        Assert.Equal(4, One(d, "inline-comment").Line);
        Assert.DoesNotContain(V("ADEVICE a\nACHANNELS 2\nMYCALL K1ABC\nCHANNEL 0\nMODEM 1200\nCHANNEL 1\nMODEM 9600\n"), x => x.Code == "overridden");
    }

    [Fact]
    public void Obsolete_and_other_contradictions()
    {
        One(V("MYCALL K1ABC\nBEACON 0 1 2 x\n"), "beacon-obsolete");
        One(V("MYCALL K1ABC\nFULLDUP maybe\n"), "fulldup");
        One(V("ADEVICE a\nADEVICE b\nMYCALL K1ABC\n"), "adevice-twice");
        One(V("MYCALL K1ABC\nPBEACON every=10 sendto=3\n"), "beacon-channel");
        One(V("MYCALL K1ABC\nIGTXVIA 3 WIDE1-1\n"), "igtxvia-channel");
    }

    [Fact]
    public void Repository_samples_validate_without_unknown_directives()
    {
        foreach (var f in new[] { "test/check-config/good.conf", "test/compat/configs/kitchen_sink.conf", "conf/sdr.conf" })
        {
            var p = Path.Combine(TestEnv.RepoRoot, f);
            if (!File.Exists(p)) continue;
            var d = ConfigValidator.Validate(ConfigDocument.Load(p));
            Assert.DoesNotContain(d, x => x.Code == "unknown-directive");
        }
    }
}
