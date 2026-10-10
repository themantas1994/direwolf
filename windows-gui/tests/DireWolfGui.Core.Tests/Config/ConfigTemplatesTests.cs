using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Tests.Config;

public class ConfigTemplatesTests
{
    internal static NewStationOptions Basic(int speed = 1200, string? wapr = null) => new()
    {
        Callsign = "k1abc-7",
        AudioDevices = [new AudioDeviceOption { Input = "USB Audio CODEC" }],
        Channels = [new ChannelOption { Channel = 0, ModemSpeed = speed, WaprProfile = wapr, Ptt = PttMethod.SerialRts, PttSerialPort = "com3" }],
    };

    [Fact]
    public void Basic_template_is_safe_and_commented()
    {
        string text = ConfigTemplates.NewStation(Basic());
        var doc = ConfigDocument.Parse(text);
        Assert.Equal(text, doc.ToText());
        Assert.Equal(["USB Audio CODEC", "USB Audio CODEC"], doc.FindDirective("ADEVICE")!.Arguments);
        Assert.Equal(["K1ABC-7"], doc.FindDirective("MYCALL", 0)!.Arguments);
        Assert.Equal(["1200"], doc.FindDirective("MODEM", 0)!.Arguments);
        Assert.Equal(["COM3", "RTS"], doc.FindDirective("PTT", 0)!.Arguments);
        Assert.Equal(["LOCAL"], doc.FindDirective("TCPBIND")!.Arguments);
        Assert.Contains(doc.Lines, l => l.Kind == ConfigLineKind.Comment && l.Text.Contains("TCPBIND LOCAL"));
        foreach (var k in new[] { "PBEACON", "DIGIPEAT", "IGSERVER", "IGLOGIN", "IGTXVIA" }) Assert.Empty(doc.FindDirectives(k));
        Assert.DoesNotContain(doc.Directives, l => l.TrailingComment != null);
        var v = ConfigValidator.Validate(doc);
        Assert.DoesNotContain(v, d => d.Severity >= DiagnosticSeverity.Warning);
        var services = StationServicesAnalyzer.Analyze(doc);
        Assert.All(services.Services, s => Assert.Equal(StationServiceKind.ClientPort, s.Kind));
    }

    [Fact]
    public void Remote_clients_option_emits_tcpbind_any()
    {
        var o = Basic();
        o.AllowRemoteClients = true;
        var doc = ConfigDocument.Parse(ConfigTemplates.NewStation(o));
        Assert.Equal(["ANY"], doc.FindDirective("TCPBIND")!.Arguments);
        Assert.Contains(ConfigValidator.Validate(doc), d => d.Code == "network-exposed");
    }

    [Fact]
    public void Requested_services_are_commented_out_by_default()
    {
        var o = Basic();
        o.IncludeBeacon = o.IncludeDigipeater = o.IncludeIGate = true;
        o.IGatePasscode = "12345";
        string text = ConfigTemplates.NewStation(o);
        var doc = ConfigDocument.Parse(text);
        foreach (var k in new[] { "PBEACON", "DIGIPEAT", "IGSERVER", "IGLOGIN", "IGTXVIA" }) Assert.Empty(doc.FindDirectives(k));
        Assert.Contains("#PBEACON", text);
        Assert.Contains("#DIGIPEAT 0 0", text);
        Assert.DoesNotContain("12345", text);
    }

    [Fact]
    public void Activated_services_are_written_but_internet_to_rf_stays_off()
    {
        var o = Basic();
        o.IncludeBeacon = o.IncludeDigipeater = o.IncludeIGate = o.ActivateOptionalServices = true;
        o.Latitude = 42.5; o.Longitude = -71.25; o.BeaconComment = "Dire Wolf station";
        o.IGatePasscode = "12345";
        var doc = ConfigDocument.Parse(ConfigTemplates.NewStation(o));
        Assert.Single(doc.FindDirectives("PBEACON"));
        Assert.Single(doc.FindDirectives("DIGIPEAT"));
        Assert.Equal(["K1ABC-7", "12345"], doc.FindDirective("IGLOGIN")!.Arguments);
        Assert.Empty(doc.FindDirectives("IGTXVIA"));
        Assert.Contains("comment=Dire Wolf station", doc.FindDirective("PBEACON")!.Arguments);
    }

    [Fact]
    public void Wapr_only_when_chosen_and_explained()
    {
        Assert.DoesNotContain("MODEM WAPR", ConfigTemplates.NewStation(Basic()));
        var o = Basic(wapr: "h150");
        o.Channels[0].WaprAirtimePercent = 10;
        string text = ConfigTemplates.NewStation(o);
        Assert.Contains("MODEM WAPR H150 AIRTIME=10", text);
        Assert.Contains("EXPERIMENTAL", text);
        Assert.Contains("unverified", text);
        Assert.DoesNotContain("FX25TX", text);
    }

    [Fact]
    public void Two_radios_stereo_and_cm108()
    {
        var o = new NewStationOptions
        {
            Callsign = "K1ABC",
            AudioDevices = [new AudioDeviceOption { Input = "1", Output = "2", Stereo = true, SampleRate = 48000 }],
            Channels =
            [
                new ChannelOption { Channel = 0, ModemSpeed = 1200, Ptt = PttMethod.Cm108 },
                new ChannelOption { Channel = 1, ModemSpeed = 9600, MyCall = "K1ABC-2", Ptt = PttMethod.SerialDtr, PttSerialPort = "COM4", PttInvert = true },
            ],
            KissPort = 0,
        };
        var doc = ConfigDocument.Parse(ConfigTemplates.NewStation(o));
        Assert.Equal(["2"], doc.FindDirective("ACHANNELS", 0)!.Arguments);
        Assert.Equal(["CM108"], doc.FindDirective("PTT", 0)!.Arguments);
        Assert.Equal(["COM4", "-DTR"], doc.FindDirective("PTT", 1)!.Arguments);
        Assert.Equal(["K1ABC-2"], doc.FindDirective("MYCALL", 1)!.Arguments);
        Assert.Equal(["0"], doc.FindDirective("KISSPORT")!.Arguments);
        Assert.DoesNotContain(ConfigValidator.Validate(doc), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("N0CALL-99")]
    [InlineData("")]
    public void Bad_callsign_rejected(string call)
    {
        var o = Basic();
        o.Callsign = call;
        Assert.Throws<ArgumentException>(() => ConfigTemplates.NewStation(o));
    }

    [Fact]
    public void Invalid_options_rejected()
    {
        var o = Basic();
        o.Channels[0].Channel = 1; // mono device
        Assert.Throws<ArgumentException>(() => ConfigTemplates.NewStation(o));
        o = Basic(speed: 1234);
        Assert.Throws<ArgumentException>(() => ConfigTemplates.NewStation(o));
        o = Basic(wapr: "X99");
        Assert.Throws<ArgumentException>(() => ConfigTemplates.NewStation(o));
        o = Basic();
        o.Channels[0].PttSerialPort = null;
        Assert.Throws<ArgumentException>(() => ConfigTemplates.NewStation(o));
    }

    [Fact]
    public void Windows_ptt_methods_exclude_unsupported_ones()
    {
        var names = ConfigTemplates.WindowsPttMethods.Select(m => m.Method.ToString()).ToList();
        Assert.DoesNotContain("Gpio", names);
        Assert.Contains(PttMethod.Cm108, ConfigTemplates.WindowsPttMethods.Select(m => m.Method));
    }
}
