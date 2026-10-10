using DireWolfGui.Core.Config;
using DireWolfGui.Core.Wapr;

namespace DireWolfGui.Core.Tests.Wapr;

public class WaprSupportTests
{
    [Fact]
    public void Profiles_match_usage_document()
    {
        Assert.Equal(["F600", "H150", "R25"], WaprSupport.Profiles.Select(p => p.Name));
        var f = WaprSupport.FindProfile("f600")!;
        Assert.Equal((600.0, 4, 900.0, 2700.0, 0.67), (f.SymbolRate, f.Tones, f.LowToneHz, f.HighToneHz, f.FrameSeconds));
        var r = WaprSupport.FindProfile("R25")!;
        Assert.Equal((8, 11.96), (r.Tones, r.FrameSeconds));
        Assert.Equal("4 tones, 1275-1725 Hz", WaprSupport.FindProfile("H150")!.ToneDescription);
        Assert.Null(WaprSupport.FindProfile("X99"));
        Assert.False(WaprSupport.IsValidProfile(null));
    }

    [Fact]
    public void Profiles_agree_with_usage_md_table()
    {
        string usage = File.ReadAllText(Path.Combine(Tests.Config.TestEnv.RepoRoot, "doc", "wapr", "USAGE.md"));
        foreach (var p in WaprSupport.Profiles)
        {
            Assert.Contains($"`{p.Name}`", usage);
            Assert.Contains($"{p.FrameSeconds:0.00} s", usage);
        }
    }

    [Theory]
    [InlineData("AIRTIME=10", true, 10)]
    [InlineData("airtime=0.5", true, 0.5)]
    [InlineData("AIRTIME=100", true, 100)]
    [InlineData("AIRTIME=0", false, 0)]
    [InlineData("AIRTIME=101", false, 0)]
    [InlineData("SPEED=10", false, 0)]
    public void Airtime_option(string token, bool ok, double pct)
    {
        Assert.Equal(ok, WaprSupport.TryParseAirtime(token, out double p));
        if (ok) Assert.Equal(pct, p);
    }

    [Theory]
    [InlineData(null, 0xFF)]
    [InlineData("POS,MSG", 0x05)]
    [InlineData("pos,status,obj,item,wx,tlm,other", 0xFB)]
    [InlineData("ALL", 0xFF)]
    [InlineData("POS,BAD", 0)]
    public void Gate_types(string? list, int bits)
    {
        Assert.Equal(bits, WaprSupport.ParseGateTypes(list));
        if (bits != 0) Assert.Equal(bits, WaprSupport.ParseGateTypes(WaprSupport.DescribeGateTypes(bits)));
    }

    [Fact]
    public void Duty_cycle_estimates_and_thresholds()
    {
        var h150 = WaprSupport.FindProfile("H150")!;
        Assert.Equal(3.07, WaprSupport.SecondsPerFrame(h150), 2);
        var low = WaprSupport.EstimateDuty(h150, 2); // two frames an hour
        Assert.Equal(DutyLevel.Ok, low.Level);
        Assert.InRange(low.DutyPercent, 0.16, 0.18);
        var r25 = WaprSupport.FindProfile("R25")!;
        var busy = WaprSupport.EstimateDuty(r25, 90, airtimeLimitPercent: 10); // every 40 s: 31 %
        Assert.Equal(DutyLevel.High, busy.Level);
        Assert.Contains("exceeds AIRTIME=10", busy.Explanation);
        Assert.Equal(DutyLevel.Excessive, WaprSupport.EstimateDuty(r25, 200).Level);
        Assert.Equal(DutyLevel.Elevated, WaprSupport.EstimateDuty(r25, 60).Level); // every minute: 21 %
        Assert.Equal(19, WaprSupport.FramesPerAirtimeWindow(h150, 10)); // 60 s / 3.07 s
    }

    [Fact]
    public void Payload_limit()
    {
        Assert.True(WaprSupport.FitsPayload(new string('x', 32), out int n));
        Assert.Equal(32, n);
        Assert.False(WaprSupport.FitsPayload(new string('é', 17), out n));
        Assert.Equal(34, n);
    }

    [Fact]
    public void Supported_only_when_build_reports_wapr()
    {
        Assert.False(WaprSupport.IsSupported(null, out string why));
        Assert.Contains("not been checked", why);
        var s = ConfigChecker.ParseSummary(["check-config: features fx25 il2p"]);
        Assert.False(WaprSupport.IsSupported(s, out why));
        Assert.Equal(WaprSupport.NotSupportedExplanation, why);
        s = ConfigChecker.ParseSummary(["check-config: features wapr fx25 il2p cm108"]);
        Assert.True(WaprSupport.IsSupported(s));
        Assert.Contains("simulation only", WaprSupport.ExperimentalNotice);
        Assert.Contains("not been verified", WaprSupport.ExperimentalNotice);
    }
}
