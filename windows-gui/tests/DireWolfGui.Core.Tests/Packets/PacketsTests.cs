using System.Text;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Packets;

namespace DireWolfGui.Core.Tests.Packets;

public class ConsoleOutputParserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    // Captured from a real "direwolf -t 0 -c x.conf -l logs -a 10 -" run fed with gen_packets audio.
    private static readonly string[] RealRun =
    {
        "Dire Wolf Release 1.8.2, November 2025",
        "Includes optional support for:  cm108-ptt",
        "",
        "Reading config file /tmp/x.conf",
        "Audio input device for receive: stdin  (channel 0)",
        "Audio out device for transmit: null  (channel 0)",
        "Channel 0: 1200 baud, AFSK 1200 & 2200 Hz, A+, 44100 sample rate, Tx AX.25.",
        "",
        "Note: PTT not configured for channel 0. (OK if using VOX.)",
        "Ready to accept AGW client application 0 on port 18000 ...",
        "Ready to accept KISS TCP client application 0 on port 18001 ...",
        "",
        "WB2OSZ-15 audio level = 50(14/14)    _||||||__",
        "[0.3] WB2OSZ-15>APDW12,WIDE2-1:!4237.14NS07120.83W#PHG7140Chelmsford MA<0x0a>",
        "Position, SSn-N digipeater (includes WIDEn-N), WB2OSZ DireWolf, 49 W height(HAAT)=20ft=6m 4dBi omni",
        "N 42 37.1400, W 071 20.8300",
        "Chelmsford MA",
        "Opening log file \"2026-10-10.log\".",
        "",
        "N0ABC audio level = 50(14/14)    _||||||__",
        "[0.3] N0ABC>APRS::N0TEST   :Hello there{12",
        "APRS Message, number \"12\", from \"N0ABC\" to \"N0TEST\"",
        "Hello there",
        "",
        "ADEVICE0: Sample rate approx. 44.1 k, 0 errors, receive audio level CH0 73",
        "",
        "End of file on stdin.  Exiting.",
    };

    [Fact]
    public void ParsesRealRun()
    {
        var p = new ConsoleOutputParser();
        var packets = new List<PacketRecord>();
        var notices = new List<ConsoleNotice>();
        var levels = new List<AudioLevelReading>();
        var stats = new List<AudioStatisticsReading>();
        p.PacketParsed += packets.Add;
        p.Notice += notices.Add;
        p.AudioLevel += levels.Add;
        p.AudioStatistics += stats.Add;
        foreach (var l in RealRun) p.ProcessLine(l, T0);

        Assert.Equal("Release 1.8.2, November 2025", p.Version);
        Assert.Equal(2, packets.Count);
        var a = packets[0];
        Assert.Equal(0, a.Channel);
        Assert.Equal("0.3", a.Label);
        Assert.Equal(PacketDirection.Received, a.Direction);
        Assert.Equal(PacketOrigin.Radio, a.Origin);
        Assert.Equal("WB2OSZ-15", a.Source);
        Assert.Equal("APDW12", a.Destination);
        Assert.Equal(new[] { "WIDE2-1" }, a.Path);
        Assert.Equal("!4237.14NS07120.83W#PHG7140Chelmsford MA\n", a.Info);
        Assert.Equal(50, a.AudioLevel);
        Assert.Equal("50(14/14)", a.AudioLevelText);
        Assert.Equal(3, a.DecodedLines.Count);   // "Opening log file" is not part of the decode
        Assert.Equal("N 42 37.1400, W 071 20.8300", a.DecodedLines[1]);
        Assert.NotNull(a.Aprs);
        Assert.Equal(42.619, a.Aprs!.Latitude!.Value, 3);
        Assert.Equal(-71.347167, a.Aprs.Longitude!.Value, 5);
        Assert.Equal("S#", a.Aprs.Symbol);

        var m = packets[1];
        Assert.Equal(AprsPacketType.Message, m.Aprs!.Type);
        Assert.Equal("N0TEST", m.Aprs.Addressee);
        Assert.Equal("12", m.Aprs.MessageId);
        Assert.Equal(2, m.DecodedLines.Count);

        Assert.Equal(2, levels.Count);
        Assert.Equal(0, levels[0].Channel);
        Assert.Equal(14, levels[0].Mark);

        Assert.Contains(notices, n => n.Kind == ConsoleNoticeKind.ServerListening && n.Protocol == "AGW" && n.Port == 18000);
        Assert.Contains(notices, n => n.Kind == ConsoleNoticeKind.ServerListening && n.Protocol == "KISS" && n.Port == 18001);
        Assert.Contains(notices, n => n.Kind == ConsoleNoticeKind.AudioDevice && n.Channel == 0);
        Assert.Contains(notices, n => n.Kind == ConsoleNoticeKind.AudioInputEnded);
        Assert.Contains(notices, n => n.Kind == ConsoleNoticeKind.Startup);
        Assert.Single(stats);
        Assert.Equal(73, stats[0].ChannelLevels[0]);
        Assert.Equal(44.1, stats[0].SampleRateKHz);
    }

    [Fact]
    public void TransmitIgateAndOtherLabels()
    {
        var p = new ConsoleOutputParser();
        var packets = new List<PacketRecord>();
        var notices = new List<ConsoleNotice>();
        p.PacketParsed += packets.Add;
        p.Notice += notices.Add;
        p.ProcessLine("[0L] N0TEST>APDW18,WIDE2-1::N0ABC    :hi{3", T0);
        p.ProcessLine("[1H 12:34:56] N0TEST>APDW18:>status", T0);
        p.ProcessLine("[0>is] N0TEST>APDW18:>beacon", T0);
        p.ProcessLine("[rx>ig] N1ZKO-7>T2TS7X,WIDE1-1,WIDE2-1,qAR,WB2OSZ-14:`c6wl!i[/>\"4]}[scanning]=", T0);
        p.ProcessLine("[ig] # logresp WB2OSZ-14 verified, server T2BC", T0);
        p.ProcessLine("[ig>tx] ANSRVR>APWW11,KJ4ERJ-15*,TCPIP*,qAS,KJ4ERJ-15::AA1PR-9  :N:HOTG 161 Messages Sent{JL}", T0);
        p.ProcessLine("[0.is] W1ABC>APRS,TCPIP*:>from internet", T0);
        p.ProcessLine("[0.dtmf] WB4APR>APDW18:t2551", T0);
        p.ProcessLine("[0>nt] N0TEST>APDW18:>to net tnc", T0);
        p.ProcessLine("[WAPR gate 1>0] not gated, duplicate: N0BBB", T0);
        p.ProcessLine("WAPR channel 1: N0BBB acknowledged frame 77.", T0);
        p.ProcessLine("WAPR channel 1: not sent, airtime limit reached: N0AAA>APWAPR:hello", T0);

        Assert.Equal(8, packets.Count);
        Assert.Equal((PacketDirection.Transmitted, PacketOrigin.Local, 0), (packets[0].Direction, packets[0].Origin, packets[0].Channel!.Value));
        Assert.Equal(1, packets[1].Channel);
        Assert.Equal("1H", packets[1].Label);
        Assert.Equal(PacketOrigin.ToAprsIs, packets[2].Origin);
        Assert.Equal(PacketOrigin.ToAprsIs, packets[3].Origin);
        Assert.Equal(PacketOrigin.IgateToRadio, packets[4].Origin);
        Assert.Equal("ANSRVR", packets[4].Source);
        Assert.Equal(new[] { "KJ4ERJ-15*", "TCPIP*", "qAS", "KJ4ERJ-15" }, packets[4].Path);
        Assert.Equal(PacketOrigin.FromAprsIs, packets[5].Origin);
        Assert.Equal(PacketOrigin.Dtmf, packets[6].Origin);
        Assert.Equal(PacketOrigin.NetworkTnc, packets[7].Origin);
        Assert.Equal(AprsPacketType.MicE, packets[3].Aprs!.Type);
        Assert.Contains(notices, n => n.Kind == ConsoleNoticeKind.IGate);
        Assert.Contains(notices, n => n.Kind == ConsoleNoticeKind.WaprGate);
        Assert.Equal(2, notices.Count(n => n.Kind == ConsoleNoticeKind.WaprLink && n.Channel == 1));
    }

    [Fact]
    public void NonAprsFrameDescriptionAndFecAndWapr()
    {
        var p = new ConsoleOutputParser();
        var packets = new List<PacketRecord>();
        p.PacketParsed += packets.Add;
        p.ProcessLine("", T0);
        p.ProcessLine("N0ABC audio level = 62(30/25)   FX.25    __|||||_", T0);
        p.ProcessLine("[0] N0ABC>N0TEST:(SABM cmd, p=1)", T0);
        p.ProcessLine("", T0);
        p.ProcessLine("N0ABC audio level = 62(30/25)   [NONE]   __|||||_", T0);
        p.ProcessLine("[0] N0ABC>N0TEST:(I cmd, n(s)=0, n(r)=0, p=0, pid=0xf0)hello<0x0d>", T0);
        p.ProcessLine("", T0);
        p.ProcessLine("N0BBB audio level = 40   WAPR   SNR 12 dB, +3 Hz", T0);
        p.ProcessLine("[1] N0BBB>APWAPR:>wapr test", T0);
        p.ProcessLine("", T0);
        p.ProcessLine("Digipeater WIDE2 (probably N0RPT) audio level = 30(10/9)  [NONE]  ___|||___", T0);
        p.ProcessLine("[0.1] N0ABC>APRS,N0RPT,WIDE2*:>x", T0);

        Assert.Equal("SABM cmd, p=1", packets[0].FrameDescription);
        Assert.Equal("FX.25", packets[0].Fec);
        Assert.Null(packets[0].Aprs);
        Assert.Equal("hello\r", packets[1].Info);
        Assert.Equal("[NONE]", packets[1].Fec);
        Assert.True(packets[2].IsWapr);
        Assert.Equal("SNR 12 dB, +3 Hz", packets[2].WaprDetails);
        Assert.Equal(40, packets[2].AudioLevel);
        Assert.Equal("WIDE2", packets[3].Heard);
        Assert.Equal(30, packets[3].AudioLevel);
    }

    [Fact]
    public void ErrorsWarningsAndClients()
    {
        var p = new ConsoleOutputParser();
        var notices = new List<ConsoleNotice>();
        p.Notice += notices.Add;
        p.ProcessLine("ERROR - Could not open configuration file nonexist.conf in cwd or homedir.", T0);
        p.ProcessLine("Could not open audio device plughw:1,0 for input", T0);
        p.ProcessLine("Line 7: Invalid channel number.", T0);
        p.ProcessLine("Attached to AGW client application 0 ...", T0);
        p.ProcessLine("Error getting message header from AGW client application 0.", T0);
        p.ProcessLine("QRT", T0);
        p.ProcessLine("Audio input level is too high. This may cause distortion and reduced decode performance.", T0);
        Assert.Equal(ConsoleNoticeKind.ConfigProblem, notices[0].Kind);
        Assert.Equal(ConsoleNoticeKind.Error, notices[1].Kind);
        Assert.Equal(ConsoleNoticeKind.ConfigProblem, notices[2].Kind);
        Assert.Equal(ConsoleNoticeKind.ClientConnected, notices[3].Kind);
        Assert.Equal(ConsoleNoticeKind.ClientDisconnected, notices[4].Kind);
        Assert.Equal("AGW", notices[4].Protocol);
        Assert.Equal(ConsoleNoticeKind.Shutdown, notices[5].Kind);
        Assert.Equal(ConsoleNoticeKind.Warning, notices[6].Kind);
    }

    [Fact]
    public void UnescapeHandlesUtf8()
    {
        Assert.Equal("a\nb", ConsoleOutputParser.Unescape("a<0x0a>b"));
        Assert.Equal("é", ConsoleOutputParser.Unescape("<0xc3><0xa9>"));
        Assert.Equal("ÿ", ConsoleOutputParser.Unescape("<0xff>"));
    }
}

public class Ax25FrameTests
{
    [Fact]
    public void UiRoundTrip()
    {
        var f = Ax25Frame.CreateUi("N0TEST-7", "APDW18", new[] { "WIDE1-1", "WIDE2-2" }, ">hello");
        var bytes = f.Encode();
        Assert.Equal(7 * 4 + 2 + 6, bytes.Length);
        Assert.True(Ax25Frame.TryDecode(bytes, out var d, out var err), err);
        Assert.Equal("N0TEST-7", d!.Source.ToString());
        Assert.Equal("APDW18", d.Destination.ToString());
        Assert.Equal(Ax25FrameType.UI, d.FrameType);
        Assert.Equal("cmd", d.CommandResponse);
        Assert.Equal((byte)0xF0, d.Pid);
        Assert.Equal("N0TEST-7>APDW18,WIDE1-1,WIDE2-2:>hello", d.ToMonitorString());
    }

    [Fact]
    public void ControlFieldDescriptions()
    {
        static Ax25Frame F(byte control, bool dstC, bool srcC, byte? pid = null, string info = "") =>
            new(new Ax25Address("N0B", 0, dstC), new Ax25Address("N0A", 0, srcC), Array.Empty<Ax25Address>(), control, pid, Encoding.ASCII.GetBytes(info));
        Assert.Equal("SABM cmd, p=1", F(0x3F, true, false).Describe());
        Assert.Equal("UA res, f=1", F(0x73, false, true).Describe());
        Assert.Equal("DISC cmd, p=0", F(0x43, true, false).Describe());
        Assert.Equal("DM res, f=0", F(0x0F, false, true).Describe());
        Assert.Equal("RR res, n(r)=3, f=1", F(0x71, false, true).Describe());
        Assert.Equal("RNR cmd, n(r)=2, p=0", F(0x45, true, false).Describe());
        Assert.Equal("REJ res, n(r)=5, f=0", F(0xA9, false, true).Describe());
        Assert.Equal("I cmd, n(s)=2, n(r)=1, p=0, pid=0xf0", F(0x24, true, false, 0xF0).Describe());
        Assert.Equal("FRMR res, f=0", F(0x87, false, true).Describe());
        Assert.Equal("XID cmd, p=1", F(0xBF, true, false).Describe());
        Assert.Equal("TEST cmd, p=0", F(0xE3, true, false).Describe());
        Assert.Equal("UI cc=00, p/f=0", F(0x03, false, false, 0xF0).Describe());
        var i = F(0x24, true, false, 0xF0, "hi\r");
        Assert.Equal(2, i.NS);
        Assert.Equal(1, i.NR);
        Assert.Equal("N0A>N0B:(I cmd, n(s)=2, n(r)=1, p=0, pid=0xf0)hi<0x0d>", i.ToMonitorString());
        var roundTrip = i.Encode();
        Assert.True(Ax25Frame.TryDecode(roundTrip, out var back, out _));
        Assert.Equal(i.ToMonitorString(), back!.ToMonitorString());
    }

    [Fact]
    public void DecodeRejectsGarbage()
    {
        Assert.False(Ax25Frame.TryDecode(new byte[5], out _, out _));
        var noEnd = new byte[7 * 3];   // no address extension bit anywhere
        Assert.False(Ax25Frame.TryDecode(noEnd, out _, out var e));
        Assert.NotNull(e);
    }

    [Fact]
    public void RepeatedFlagShownWithStar()
    {
        var f = new Ax25Frame(new Ax25Address("APRS", 0, true), new Ax25Address("N0A", 1), new[] { new Ax25Address("N0RPT", 0, true), new Ax25Address("WIDE2", 1) }, 3, 0xF0, Encoding.ASCII.GetBytes(">x"));
        Assert.Equal("N0A-1>APRS,N0RPT*,WIDE2-1:>x", f.ToMonitorString());
        Assert.True(Ax25Address.TryParse("wide2-1*", out var a));
        Assert.True(a.HBit);
        Assert.False(Ax25Address.TryParse("TOOLONGCALL", out _));
        Assert.False(Ax25Address.TryParse("N0A-16", out _));
    }
}

public class PacketStoreTests
{
    [Fact]
    public void CountsPerChannel()
    {
        var s = new PacketStore(3);
        var t = DateTimeOffset.UtcNow;
        s.Add(new PacketRecord { Time = t, Channel = 0, Direction = PacketDirection.Received, AudioLevel = 40 });
        s.Add(new PacketRecord { Time = t, Channel = 0, Direction = PacketDirection.Transmitted });
        s.Add(new PacketRecord { Time = t, Channel = 1, Direction = PacketDirection.Received });
        s.Add(new PacketRecord { Time = t, Channel = 0, Direction = PacketDirection.Received });
        var a = s.GetChannelActivity();
        Assert.Equal(2, a.Count);
        Assert.Equal((2L, 1L, 40), (a[0].Received, a[0].Transmitted, a[0].LastAudioLevel!.Value));
        Assert.Equal(3, s.Snapshot().Count);
        Assert.Equal(4, s.LastSequence);
        Assert.Single(s.GetSince(3));
    }
}
