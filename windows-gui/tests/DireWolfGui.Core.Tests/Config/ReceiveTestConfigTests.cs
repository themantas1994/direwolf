using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Tests.Config;

/// <summary>
/// The Setup wizard's receive-only test must not be able to transmit or forward anything:
/// no beacons, digipeating, IGate, WAPR gate, PTT, and no client ports through which another
/// program could transmit.
/// </summary>
public class ReceiveTestConfigTests
{
    private const string FullStation = """
        ADEVICE "USB Audio CODEC"
        ACHANNELS 1
        CHANNEL 0
        MYCALL K1ABC-7
        MODEM 1200
        PTT COM3 RTS
        TXDELAY 30
        AGWPORT 8000
        KISSPORT 8001
        KISSPORT 8002 0
        DIGIPEAT 0 0 ^WIDE[3-7]-[1-7]$|^TEST$ ^WIDE[12]-[12]$ TRACE
        IGSERVER noam.aprs2.net
        IGLOGIN K1ABC-7 12345
        IGTXVIA 0 WIDE1-1
        PBEACON delay=1 every=30 symbol="digi" lat=42^37.14N long=071^20.83W
        CBEACON delay=1 every=10 info="hello"
        # a comment that must survive
        """;

    private static ConfigDocument Station() => ConfigDocument.Parse(FullStation.Replace("\r\n", "\n") + "\n");

    [Fact]
    public void Full_station_becomes_receive_only_and_keeps_audio_and_modem()
    {
        var doc = ReceiveTestConfig.MakeReceiveOnly(Station());
        Assert.Empty(ReceiveTestConfig.Verify(doc));
        Assert.Equal(["1200"], doc.FindDirective("MODEM", 0)!.Arguments);
        Assert.Equal(["K1ABC-7"], doc.FindDirective("MYCALL", 0)!.Arguments);
        Assert.NotNull(doc.FindDirective("ADEVICE"));
        Assert.Contains("# a comment that must survive", doc.ToText());
        Assert.DoesNotContain("12345", doc.ToText());     // passcode masked even in the disabled line
    }

    [Fact]
    public void Verify_names_every_way_the_original_could_transmit()
    {
        var problems = ReceiveTestConfig.Verify(Station());
        foreach (var expected in new[] { "beacon", "digipeating", "IGate", "AGW", "KISS", "PTT" })
            Assert.Contains(problems, p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Options_for_the_test_switch_off_everything_that_transmits()
    {
        var o = ConfigTemplatesTests.Basic();
        o.IncludeBeacon = o.IncludeDigipeater = o.IncludeIGate = true;
        var test = ReceiveTestConfig.ReceiveOnlyOptions(o);
        Assert.Empty(ReceiveTestConfig.Verify(ConfigDocument.Parse(ConfigTemplates.NewStation(test))));
    }

    [DireWolfFact]
    public async Task Dire_Wolfs_own_parser_sees_no_ports_beacons_digipeater_igate_or_ptt()
    {
        var doc = ReceiveTestConfig.MakeReceiveOnly(Station());
        doc.SetDirective("ADEVICE", "stdin null");   // parse without a Windows sound card
        var r = await ConfigChecker.CheckDocumentAsync(TestEnv.DireWolfExe, doc);
        var s = r.Summary!;
        Assert.Null(s.AgwPort);
        Assert.Empty(s.KissPorts);
        Assert.Empty(s.Beacons);
        Assert.Empty(s.Digipeat);
        Assert.Null(s.IGate);
        Assert.Empty(s.WaprGates);
        Assert.All(s.Channels, c => Assert.True(c.Ptt is null or "NONE"));
        Assert.True(s.BindLocalOnly);
        Assert.DoesNotContain("12345", r.RawOutput);
    }

    [Fact]
    public void Passcode_mask_round_trips_and_lines_are_recognised()
    {
        var secrets = new List<(string Call, string Secret)>();
        string text = "IGLOGIN K1ABC-7 12345\n#IGLOGIN K1ABC 777\n";
        string masked = PasscodeMask.MaskText(text, secrets);
        Assert.DoesNotContain("12345", masked);
        Assert.DoesNotContain("777", masked);
        Assert.Equal(text, PasscodeMask.UnmaskText(masked, secrets));

        Assert.True(ReceiveTestLines.IsReceivedPacket("[0.3] N0CALL>APDW18:hello"));
        Assert.True(ReceiveTestLines.IsReceivedPacket("[1] N0CALL>APDW18:hello"));
        Assert.False(ReceiveTestLines.IsReceivedPacket("[0L] N0CALL>APDW18:hello"));   // our own transmission
        Assert.Equal(50, ReceiveTestLines.AudioLevelValue("N0CALL audio level = 50(14/14)   ___|||___"));
    }
}
