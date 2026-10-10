using System.Text;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Net;
using DireWolfGui.Core.Packets;

namespace DireWolfGui.Core.Tests.Aprs;

internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan t) => Now += t;
}

internal sealed class FakeSender : IPacketSender
{
    public List<(int Port, string Src, string Dst, IReadOnlyList<string> Path, string Info)> Sent { get; } = new();
    public bool Fail { get; set; }
    public bool CanSend => !Fail;

    public Task SendUnprotoAsync(int port, string source, string destination, IReadOnlyList<string> path, string info, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("offline");
        lock (Sent) Sent.Add((port, source, destination, path, info));
        return Task.CompletedTask;
    }
}

public class AprsParserTests
{
    [Fact]
    public void UncompressedPositionWithAltitudeAndCourseSpeed()
    {
        var a = AprsParser.Parse("APRS", "!4903.50N/07201.75W-Test /A=001234");
        Assert.Equal(AprsPacketType.Position, a.Type);
        Assert.Equal(49.058333, a.Latitude!.Value, 5);
        Assert.Equal(-72.029167, a.Longitude!.Value, 5);
        Assert.Equal("/-", a.Symbol);
        Assert.Equal(1234, a.AltitudeFeet);
        Assert.Equal("Test ", a.Comment);   // Dire Wolf shows the trailing space too ("Test<0x20>")

        var b = AprsParser.Parse("APRS", "@092345z4903.50N/07201.75W>088/036Moving");
        Assert.Equal(AprsPacketType.PositionWithTimestamp, b.Type);
        Assert.Equal("092345z", b.Timestamp);
        Assert.Equal(88, b.CourseDegrees);
        Assert.Equal(36, b.SpeedKnots);
        Assert.Equal("Moving", b.Comment);
    }

    [Fact]
    public void AmbiguityAndInvalid()
    {
        var a = AprsParser.Parse("APRS", "!49  .  N/072  .  W-");
        Assert.Equal(4, a.PositionAmbiguity);
        Assert.Equal(49.0, a.Latitude!.Value, 5);
        var bad = AprsParser.Parse("APRS", "!9903.50N/07201.75W-");
        Assert.Null(bad.Latitude);
        Assert.NotNull(bad.Error);
        var shortOne = AprsParser.Parse("APRS", "!4903.5");
        Assert.False(shortOne.HasPosition);
    }

    [Fact]
    public void CompressedPositionMatchesDireWolfDecode()
    {
        // decode_aprs: N 49 30.0000, W 072 45.0002, 67 km/h, course 88
        var a = AprsParser.Parse("APRS", "=/5L!!<*e7>7P[");
        Assert.True(a.Compressed);
        Assert.Equal(49.5, a.Latitude!.Value, 4);
        Assert.Equal(-72.75, a.Longitude!.Value, 4);
        Assert.Equal(88, a.CourseDegrees);
        Assert.Equal(36.2, a.SpeedKnots!.Value, 1);
        Assert.Equal("/>", a.Symbol);
    }

    [Fact]
    public void MicEMatchesDireWolfDecode()
    {
        // decode_aprs: MIC-E, In Service, N 42 43.7800, W 071 26.9100, 0 km/h, course 177, alt 70 m, "[scanning]"
        var a = AprsParser.Parse("T2TS7X", "`c6wl!i[/>\"4]}[scanning]=");
        Assert.Equal(AprsPacketType.MicE, a.Type);
        Assert.Equal(42 + 43.78 / 60, a.Latitude!.Value, 5);
        Assert.Equal(-(71 + 26.91 / 60), a.Longitude!.Value, 5);
        Assert.Equal(177, a.CourseDegrees);
        Assert.Equal(0, a.SpeedKnots);
        Assert.Equal(70 * 3.28084, a.AltitudeFeet!.Value, 0);
        Assert.Equal("In Service", a.MicEMessage);
        Assert.Equal("[scanning]", a.Comment);
        Assert.Equal("/[", a.Symbol);

        // decode_aprs: Returning, N 33 25.6400, W 012 07.7400, 37 km/h (20 kn), course 251, alt 61 m
        var b = AprsParser.Parse("S32U6T", "`(_fn\"Oj/]\"4T}");
        Assert.Equal(33 + 25.64 / 60, b.Latitude!.Value, 5);
        Assert.Equal(-(12 + 7.74 / 60), b.Longitude!.Value, 5);
        Assert.Equal(251, b.CourseDegrees);
        Assert.Equal(20, b.SpeedKnots);
        Assert.Equal("Returning", b.MicEMessage);
    }

    [Fact]
    public void MicEInvalidDestinationNeverGuesses()
    {
        var a = AprsParser.Parse("APRS", "`c6wl!i[/>\"4]}");
        Assert.Equal(AprsPacketType.MicE, a.Type);
        Assert.False(a.HasPosition);
        Assert.NotNull(a.Error);
        Assert.False(AprsParser.Parse("T2TS", "`c6wl!i[/").HasPosition);
    }

    [Fact]
    public void ObjectsAndItems()
    {
        var o = AprsParser.Parse("APDW18", ";LEADER   *092345z4903.50N/07201.75W>088/036");
        Assert.Equal(AprsPacketType.Object, o.Type);
        Assert.Equal("LEADER", o.ObjectName);
        Assert.True(o.ObjectAlive);
        Assert.Equal(88, o.CourseDegrees);
        var k = AprsParser.Parse("APDW18", ";LEADER   _092345z4903.50N/07201.75W>");
        Assert.False(k.ObjectAlive);
        var i = AprsParser.Parse("APDW18", ")AID #2!4903.50N/07201.75WA");
        Assert.Equal(AprsPacketType.Item, i.Type);
        Assert.Equal("AID #2", i.ObjectName);
        Assert.True(i.HasPosition);
    }

    [Fact]
    public void Messages()
    {
        var m = AprsParser.Parse("APRS", ":N0TEST   :Hello there{12");
        Assert.Equal((AprsPacketType.Message, "N0TEST", "Hello there", "12"), (m.Type, m.Addressee, m.MessageText, m.MessageId));
        var r = AprsParser.Parse("APRS", ":N0TEST-7 :Reply{MM}AA");
        Assert.Equal(("MM", "AA"), (r.MessageId, r.ReplyAck));
        var ack = AprsParser.Parse("APRS", ":N0TEST   :ack12");
        Assert.Equal((AprsPacketType.MessageAck, "12"), (ack.Type, ack.MessageId));
        var rej = AprsParser.Parse("APRS", ":N0TEST   :rej5");
        Assert.Equal(AprsPacketType.MessageReject, rej.Type);
        var bln = AprsParser.Parse("APRS", ":BLN1     :Net tonight");
        Assert.Equal(AprsPacketType.Bulletin, bln.Type);
        var noId = AprsParser.Parse("APRS", ":N0TEST   :no number");
        Assert.Null(noId.MessageId);
        Assert.Equal(AprsPacketType.Other, AprsParser.Parse("APRS", ":SHORT:x").Type);
    }

    [Fact]
    public void OtherTypes()
    {
        Assert.Equal(AprsPacketType.Status, AprsParser.Parse("APRS", ">Status text").Type);
        Assert.Equal("Status text", AprsParser.Parse("APRS", ">Status text").StatusText);
        Assert.Equal(AprsPacketType.Weather, AprsParser.Parse("APRS", "_10090556c220s004g005t077r000p000P000h50b09900").Type);
        Assert.Equal(AprsPacketType.Weather, AprsParser.Parse("APRS", "!4903.50N/07201.75W_220/004g005t077").Type);
        Assert.Equal(AprsPacketType.Telemetry, AprsParser.Parse("APRS", "T#005,199,000,255,073,123,01101001").Type);
        Assert.Equal(AprsPacketType.Query, AprsParser.Parse("APRS", "?APRS?").Type);
        var tp = AprsParser.Parse("APRS", "}N0A>APRS,TCPIP,N0B*:>hi");
        Assert.Equal(AprsPacketType.ThirdParty, tp.Type);
        Assert.Equal("N0A>APRS,TCPIP,N0B*:>hi", tp.ThirdPartyPayload);
        Assert.Equal(AprsPacketType.Other, AprsParser.Parse("APRS", "$GPRMC,1").Type);
        Assert.Equal(AprsPacketType.Other, AprsParser.Parse("APRS", "").Type);
        Assert.True(AprsParser.Parse("ID", "TNC beacon !4903.50N/07201.75W-").HasPosition);
    }
}

public class DireWolfCsvLogTests
{
    private const string RealLines =
        "chan,utime,isotime,source,heard,level,error,dti,name,symbol,latitude,longitude,speed,course,altitude,frequency,offset,tone,system,status,telemetry,comment\n" +
        "0,1791644078,2026-10-10T14:54:38Z,WB2OSZ-15,WB2OSZ-15,50(14/14),0,!,WB2OSZ-15,S#,42.619000,-71.347167,,,,,,,WB2OSZ DireWolf,,,Chelmsford MA\n" +
        "0,1791644078,2026-10-10T14:54:38Z,N0ABC,N0ABC,50(14/14),0,:,N0ABC,/ ,,,,,,,,,Unknown Unknown,,,Hello there\n";

    [Fact]
    public void ParsesRealLinesAndQuoting()
    {
        var lines = RealLines.Split('\n');
        Assert.False(DireWolfCsvLog.TryParse(lines[0], out _));
        Assert.True(DireWolfCsvLog.TryParse(lines[1], out var r));
        Assert.Equal("WB2OSZ-15", r!.Source);
        Assert.Equal(42.619, r.Latitude);
        Assert.Equal(-71.347167, r.Longitude);
        Assert.Equal(50, r.AudioLevel);
        Assert.Equal("S#", r.Symbol);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791644078), r.Time);
        Assert.True(DireWolfCsvLog.TryParse(lines[2], out var m));
        Assert.False(m!.HasPosition);

        var q = DireWolfCsvLog.SplitLine("999,1,x,N0A,N0A,,0,!,N0A,/>,1.5,2.5,,,,,,,,\"st,at\",,\"say \"\"hi\"\", ok\"");
        Assert.Equal(22, q.Count);
        Assert.Equal("st,at", q[19]);
        Assert.Equal("say \"hi\", ok", q[21]);
        Assert.True(DireWolfCsvLog.TryParse("999,1,x,N0A,N0A,,0,!,N0A,/>,1.5,2.5,,,,,,,,,,", out var own));
        Assert.True(own!.IsOwnBeacon);
    }

    [Fact]
    public void TailerFollowsAppendsPartialLinesTruncationAndRollover()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dwcsv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var time = new FakeTime(new DateTimeOffset(2026, 10, 10, 23, 59, 0, TimeSpan.Zero));
            string day1 = Path.Combine(dir, "2026-10-10.log");
            File.WriteAllText(day1, RealLines);
            var tail = new DireWolfCsvLogTailer(dir, startAtEnd: true, timeProvider: time);
            Assert.Empty(tail.Poll());   // existing content skipped

            string line = "0,1791644100,2026-10-10T14:55:00Z,N0NEW,N0NEW,40,0,!,N0NEW,/>,10.5,20.5,,,,,,,,,,c\n";
            File.AppendAllText(day1, line[..20]);
            Assert.Empty(tail.Poll());   // partial line waits
            File.AppendAllText(day1, line[20..]);
            var got = tail.Poll();
            Assert.Equal("N0NEW", Assert.Single(got).Source);

            File.WriteAllText(day1, "chan,utime\n" + line);   // truncated / rewritten
            Assert.Single(tail.Poll());

            File.AppendAllText(day1, line.Replace("N0NEW", "N0LATE"));
            time.Advance(TimeSpan.FromMinutes(2));   // UTC midnight passed
            File.WriteAllText(Path.Combine(dir, "2026-10-11.log"), "chan,utime\n" + line.Replace("N0NEW", "N0DAY2"));
            var roll = tail.Poll();
            Assert.Equal(new[] { "N0LATE", "N0DAY2" }, roll.Select(r => r.Source));
            Assert.EndsWith("2026-10-11.log", tail.CurrentFile);

            var all = new DireWolfCsvLogTailer(day1, singleFile: true, startAtEnd: false, timeProvider: time);
            Assert.Equal(2, all.Poll().Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class StationTrackerTests
{
    private static PacketRecord P(string src, string info, DateTimeOffset t, PacketDirection d = PacketDirection.Received, string dst = "APRS") =>
        new() { Time = t, Channel = 0, Direction = d, Source = src, Destination = dst, Info = info, Aprs = AprsParser.Parse(dst, info), AudioLevel = 50 };

    [Fact]
    public void TracksPositionsFromRealDataOnly()
    {
        var tr = new StationTracker { MaxTrackPoints = 2, MaxStations = 2 };
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tr.Update(P("N0A", ">just status", t));
        var s = tr.TryGet("N0A")!;
        Assert.False(s.HasPosition);
        Assert.Equal("just status", s.Status);
        tr.Update(P("N0A", "!4903.50N/07201.75W-", t.AddMinutes(1)));
        tr.Update(P("N0A", "!4903.50N/07201.75W-", t.AddMinutes(2)));   // same spot: no extra point
        tr.Update(P("N0A", "!4904.50N/07201.75W-", t.AddMinutes(3)));
        tr.Update(P("N0A", "!4905.50N/07201.75W-", t.AddMinutes(4)));
        s = tr.TryGet("N0A")!;
        Assert.Equal(5, s.PacketCount);
        Assert.Equal(2, s.Track.Count);   // bounded
        Assert.Equal(PositionSources.Packet, s.PositionSource);
        Assert.Equal(t.AddMinutes(4), s.PositionTime);

        tr.Update(P("N0TEST", ">mine", t, PacketDirection.Transmitted));
        Assert.Null(tr.TryGet("N0TEST"));

        tr.Update(P("N0B", ";OBJ      *092345z4903.50N/07201.75W>", t.AddMinutes(5)));
        var obj = tr.TryGet("OBJ")!;
        Assert.True(obj.IsObject);
        Assert.Equal("N0B", obj.ObjectOwner);

        tr.Update(P("N0C", ">x", t.AddMinutes(6)));   // exceeds MaxStations: least recently heard (N0A) dropped
        Assert.Null(tr.TryGet("N0A"));
        Assert.Equal(2, tr.Count);
    }

    [Fact]
    public void LogRecordsGivePositionsWithSource()
    {
        var tr = new StationTracker();
        Assert.True(DireWolfCsvLog.TryParse("0,1791644078,2026-10-10T14:54:38Z,WB2OSZ-15,WB2OSZ-15,50(14/14),0,!,WB2OSZ-15,S#,42.619000,-71.347167,,,,,,,WB2OSZ DireWolf,,,Chelmsford MA", out var r));
        tr.Update(r!, countAsPacket: false);
        var s = tr.TryGet("WB2OSZ-15")!;
        Assert.Equal(PositionSources.DireWolfLog, s.PositionSource);
        Assert.Equal(0, s.PacketCount);
        Assert.Equal(42.619, s.Latitude);
        Assert.Equal("Chelmsford MA", s.Comment);
    }

    [Fact]
    public void GeoMathHelpers()
    {
        double d = GeoMath.DistanceKm(42.619, -71.347167, 42.3601, -71.0589);   // Chelmsford - Boston
        Assert.InRange(d, 36, 38);
        Assert.InRange(GeoMath.BearingDegrees(0, 0, 1, 0), -0.001, 0.001);
        Assert.Equal(90, GeoMath.BearingDegrees(0, 0, 0, 1), 3);
        Assert.Equal("E", GeoMath.CompassPoint(91));
        Assert.Equal("N", GeoMath.CompassPoint(355));
    }
}

public class AprsMessageServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PacketRecord Rx(string src, string info) =>
        new() { Time = T0, Channel = 0, Direction = PacketDirection.Received, Source = src, Destination = "APRS", Info = info, Aprs = AprsParser.Parse("APRS", info) };

    [Fact]
    public async Task SendRetriesWithDoublingThenGivesUp()
    {
        var time = new FakeTime(T0);
        var sender = new FakeSender();
        var svc = new AprsMessageService(sender, time) { MyCall = "N0TEST", Path = new[] { "WIDE2-1" } };
        var m = await svc.SendAsync("n0abc", "Hello");
        Assert.Equal(AprsMessageState.Sent, m.State);
        Assert.Equal(":N0ABC    :Hello{1", sender.Sent[0].Info);
        Assert.Equal(("N0TEST", "APDW18"), (sender.Sent[0].Src, sender.Sent[0].Dst));

        time.Advance(TimeSpan.FromSeconds(29)); await svc.ProcessDueAsync();
        Assert.Single(sender.Sent);
        time.Advance(TimeSpan.FromSeconds(1)); await svc.ProcessDueAsync();
        Assert.Equal(2, sender.Sent.Count);   // retry after 30 s
        time.Advance(TimeSpan.FromSeconds(59)); await svc.ProcessDueAsync();
        Assert.Equal(2, sender.Sent.Count);
        time.Advance(TimeSpan.FromSeconds(1)); await svc.ProcessDueAsync();
        Assert.Equal(3, sender.Sent.Count);   // then after 60 s
        time.Advance(TimeSpan.FromSeconds(120)); await svc.ProcessDueAsync();
        Assert.Equal(3, sender.Sent.Count);
        Assert.Equal(AprsMessageState.GaveUp, svc.GetMessages().Single().State);
        Assert.Equal(3, svc.GetMessages().Single().Tries);
    }

    [Fact]
    public async Task AckAndRejStopRetries()
    {
        var time = new FakeTime(T0);
        var sender = new FakeSender();
        var svc = new AprsMessageService(sender, time) { MyCall = "N0TEST-7" };
        var a = await svc.SendAsync("N0ABC", "one");
        var b = await svc.SendAsync("N0ABC", "two");
        Assert.Null(svc.HandleIncoming(Rx("N0ABC", ":N0TEST   :ack1")));   // wrong SSID: not for us
        Assert.Equal(AprsMessageState.Acknowledged, svc.HandleIncoming(Rx("N0ABC", ":N0TEST-7 :ack1"))!.State);
        Assert.Equal(AprsMessageState.Rejected, svc.HandleIncoming(Rx("N0ABC", ":N0TEST-7 :rej2"))!.State);
        Assert.Null(svc.HandleIncoming(Rx("N0XYZ", ":N0TEST-7 :ack1")));   // ack from someone else
        time.Advance(TimeSpan.FromMinutes(10)); await svc.ProcessDueAsync();
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task ReplyAckAcknowledgesOurMessage()
    {
        var sender = new FakeSender();
        var svc = new AprsMessageService(sender, new FakeTime(T0)) { MyCall = "N0TEST" };
        await svc.SendAsync("N0ABC", "q");
        svc.HandleIncoming(Rx("N0ABC", ":N0TEST   :answer{XY}1"));
        var msgs = svc.GetMessages();
        Assert.Equal(AprsMessageState.Acknowledged, msgs.Single(m => m.Direction == AprsMessageDirection.Outgoing).State);
        Assert.Equal("answer", msgs.Single(m => m.Direction == AprsMessageDirection.Incoming).Text);
    }

    [Fact]
    public async Task IncomingMessagesAutoAckOnlyWhenEnabled()
    {
        var sender = new FakeSender();
        var svc = new AprsMessageService(sender, new FakeTime(T0)) { MyCall = "N0TEST", AdditionalCalls = new[] { "N0TEST-9" } };
        var r = svc.HandleIncoming(Rx("N0ABC", ":N0TEST-9 :Hi{42"));
        Assert.Equal(AprsMessageState.Received, r!.State);
        Assert.Empty(sender.Sent);   // auto-ack off by default: nothing transmitted

        svc.AutoAcknowledge = true;
        var again = svc.HandleIncoming(Rx("N0ABC", ":N0TEST-9 :Hi{42"));
        Assert.Equal(r.LocalId, again!.LocalId);   // duplicate not added twice
        await Task.Delay(50);
        Assert.Equal(":N0ABC    :ack42", Assert.Single(sender.Sent).Info);
        Assert.Single(svc.GetMessages());
        Assert.True(svc.GetMessages()[0].AckSent);

        Assert.Null(svc.HandleIncoming(Rx("N0ABC", ":N0OTHER  :not mine{1")));
        Assert.Null(svc.HandleIncoming(new PacketRecord { Direction = PacketDirection.Transmitted, Source = "N0ABC", Destination = "APRS", Info = ":N0TEST   :x{1" }));
    }

    [Fact]
    public async Task RetriesKeepTheMessagesOwnChannelAndPath()
    {
        var time = new FakeTime(T0);
        var sender = new FakeSender();
        var svc = new AprsMessageService(sender, time) { MyCall = "N0TEST", Channel = 1, Path = new[] { "WIDE1-1" } };
        await svc.SendAsync("N0ABC", "on one");
        svc.Channel = 0;                       // the composer moved on to another channel and path
        svc.Path = new[] { "WIDE2-2" };
        time.Advance(TimeSpan.FromSeconds(30)); await svc.ProcessDueAsync();
        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, s => Assert.Equal(1, s.Port));
        Assert.All(sender.Sent, s => Assert.Equal(new[] { "WIDE1-1" }, s.Path));
    }

    [Fact]
    public async Task AutoAckGoesOutOnTheChannelTheMessageWasHeardOnAndNeverForAprsIs()
    {
        var sender = new FakeSender();
        var svc = new AprsMessageService(sender, new FakeTime(T0)) { MyCall = "N0TEST", Channel = 0, AutoAcknowledge = true };
        var heard = Rx("N0ABC", ":N0TEST   :on two{5");
        svc.HandleIncoming(new PacketRecord { Time = T0, Channel = 2, Direction = PacketDirection.Received, Origin = PacketOrigin.Radio,
            Source = heard.Source, Destination = heard.Destination, Info = heard.Info, Aprs = heard.Aprs });
        await Task.Delay(50);
        Assert.Equal(2, Assert.Single(sender.Sent).Port);

        var fromIs = svc.HandleIncoming(new PacketRecord { Time = T0, Channel = 0, Direction = PacketDirection.Received, Origin = PacketOrigin.FromAprsIs,
            Source = "N0XYZ", Destination = "APRS", Info = ":N0TEST   :via internet{6", Aprs = AprsParser.Parse("APRS", ":N0TEST   :via internet{6") });
        await Task.Delay(50);
        Assert.Equal(AprsMessageState.Received, fromIs!.State);   // shown to the user...
        Assert.Single(sender.Sent);                              // ...but not acknowledged on radio
    }

    [Fact]
    public async Task ValidationAndSendFailure()
    {
        var sender = new FakeSender();
        var svc = new AprsMessageService(sender, new FakeTime(T0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SendAsync("N0ABC", "x"));   // no MyCall
        svc.MyCall = "N0TEST";
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SendAsync("N0ABC", "bad {"));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.SendAsync("N0ABC", new string('x', 68)));
        sender.Fail = true;
        var m = await svc.SendAsync("N0ABC", "x");
        Assert.Equal(AprsMessageState.GaveUp, m.State);
        Assert.Contains("offline", m.Error);
        sender.Fail = false;
        var c = await svc.SendAsync("N0ABC", "y");
        Assert.True(svc.Cancel(c.LocalId));
        Assert.Equal(AprsMessageState.Cancelled, svc.GetMessages().Last().State);
    }
}

public class ExportersTests
{
    [Fact]
    public void CsvAndGpx()
    {
        var tr = new StationTracker();
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var p = new PacketRecord { Time = t, Channel = 0, Source = "N0A", Destination = "APRS", Info = "!4903.50N/07201.75W-Hi, \"there\"", Aprs = AprsParser.Parse("APRS", "!4903.50N/07201.75W-Hi, \"there\"") };
        tr.Update(p);
        tr.Update(new PacketRecord { Time = t, Channel = 0, Source = "N0NOPOS", Destination = "APRS", Info = ">s", Aprs = AprsParser.Parse("APRS", ">s") });
        var sw = new StringWriter();
        Exporters.WritePacketsCsv(new[] { p }, sw);
        var csv = sw.ToString().Split('\n');
        Assert.StartsWith("time_utc,", csv[0]);
        Assert.Contains("\"!4903.50N/07201.75W-Hi, \"\"there\"\"\"", csv[1]);
        Assert.Equal(13, DireWolfCsvLog.SplitLine(csv[1].TrimEnd('\r')).Count);

        sw = new StringWriter();
        Exporters.WriteStationsCsv(tr.GetStations(), sw);
        Assert.Equal(3, sw.ToString().Trim().Split('\n').Length);

        sw = new StringWriter();
        Exporters.WriteGpx(tr.GetStations(), sw);
        string gpx = sw.ToString();
        Assert.Contains("<name>N0A</name>", gpx);
        Assert.DoesNotContain("N0NOPOS", gpx);
        Assert.Contains("lat=\"49.058333\"", gpx);
        System.Xml.Linq.XDocument.Parse(gpx);
        Assert.Equal("'=cmd", Exporters.CsvField("=cmd"));
        Assert.Equal("-5", Exporters.CsvField("-5"));
    }
}
