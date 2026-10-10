using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Tests.Config;

public class StationServicesTests
{
    private static StationServicesReport A(string text, CheckConfigSummary? s = null) => StationServicesAnalyzer.Analyze(ConfigDocument.Parse(text), s);

    [Fact]
    public void Receive_only_station_with_local_ports()
    {
        var r = A("MYCALL K1ABC\nMODEM 1200\nTCPBIND LOCAL\n");
        Assert.All(r.Services, s => Assert.Equal(StationServiceKind.ClientPort, s.Kind));
        Assert.Equal(2, r.Services.Count); // default AGW 8000 and KISS 8001
        Assert.All(r.Services, s => Assert.Contains("this computer only", s.Explanation));
        Assert.DoesNotContain(r.Warnings, w => w.Code == "network-exposed");
    }

    [Fact]
    public void Exposed_ports_warn()
    {
        var r = A("MYCALL K1ABC\nKISSPORT 0\nAGWPORT 8000\n");
        var agw = Assert.Single(r.Services);
        Assert.Contains("other computers", agw.Explanation);
        Assert.Contains(r.Warnings, w => w.Code == "network-exposed");
    }

    [Fact]
    public void Summary_overrides_document_for_ports()
    {
        var s = ConfigChecker.ParseSummary(["check-config: tcpbind local", "check-config: kissport 9001 chan 1"]);
        var r = A("MYCALL K1ABC\n", s);
        var k = Assert.Single(r.Services);
        Assert.Equal("KISS TCP port 9001", k.Title);
        Assert.Contains("channel 1", k.Explanation);
    }

    [Fact]
    public void Beacons_digipeater_igate_and_wapr()
    {
        var r = A("""
            ADEVICE a
            ACHANNELS 2
            CHANNEL 0
            MYCALL K1ABC
            CHANNEL 1
            MODEM WAPR R25 AIRTIME=5
            PBEACON delay=1 every=5 lat=1 long=2
            PBEACON every=0:30 sendto=1 lat=1 long=2
            IBEACON sendto=IG every=60
            DIGIPEAT 0 0 a b
            REGEN 0 0
            IGSERVER noam.aprs2.net
            IGLOGIN K1ABC 12345
            IGTXVIA 0 WIDE1-1,WIDE2-1
            WAPRGATE 1 0 POS
            WAPRGATE 1 IS
            TCPBIND LOCAL
            """.Replace("\r\n", "\n"));
        var kinds = r.Services.Select(s => s.Kind).ToList();
        Assert.Equal(3, kinds.Count(k => k == StationServiceKind.Beacon));
        Assert.Contains(StationServiceKind.Digipeater, kinds);
        Assert.Contains(StationServiceKind.Regenerator, kinds);
        Assert.Contains(StationServiceKind.IGateRfToInternet, kinds);
        var i2r = Assert.Single(r.Services, s => s.Kind == StationServiceKind.IGateInternetToRf);
        Assert.True(i2r.Transmits);
        Assert.Equal(2, kinds.Count(k => k == StationServiceKind.WaprGate));
        Assert.False(r.Services.Single(s => s.Title.Contains("APRS-IS") && s.Kind == StationServiceKind.WaprGate).Transmits);
        Assert.Contains(r.Warnings, w => w.Code == "beacon-rate" && w.Line == 7);
        Assert.Contains(r.Warnings, w => w.Code == "wapr-duty" && w.Line == 8);
        Assert.Contains(r.Warnings, w => w.Code == "regen-loop");
        Assert.Contains(r.Warnings, w => w.Code == "igate-path");
        Assert.Contains(r.Warnings, w => w.Code == "igate-txlimit");
        Assert.Contains(r.Warnings, w => w.Code == "igate-and-digi");
        Assert.True(r.TransmitsAnything && r.ForwardsAnything);
        Assert.Contains(r.Services, s => s.Kind == StationServiceKind.Beacon && s.Title.EndsWith("to APRS-IS") && !s.Transmits && s.Forwards);
    }

    [Fact]
    public void Crossband_digipeat_loop_warning()
    {
        var r = A("ADEVICE a\nACHANNELS 2\nMYCALL K1ABC\nDIGIPEAT 0 1 a b\nDIGIPEAT 1 0 a b\n");
        Assert.Equal(2, r.Warnings.Count(w => w.Code == "digi-crossband-loop"));
    }
}
