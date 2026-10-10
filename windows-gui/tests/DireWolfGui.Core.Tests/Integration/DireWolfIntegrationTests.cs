using System.Collections.Concurrent;
using DireWolfGui.Core.Aprs;
using DireWolfGui.Core.Net;
using DireWolfGui.Core.Packets;
using DireWolfGui.Core.Process;
using DireWolfGui.Core.Station;
using static DireWolfGui.Core.Tests.Integration.DireWolfTestEnvironment;

namespace DireWolfGui.Core.Tests.Integration;

[Collection("direwolf")]
public class DireWolfIntegrationTests
{
    private static readonly string[] TestPackets =
    {
        "WB2OSZ-15>APDW12,WIDE2-1:!4237.14NS07120.83W#PHG7140Chelmsford MA",
        "N1ZKO-7>T2TS7X,WIDE1-1:`c6wl!i[/>\"4]}[scanning]=",
        "N0ABC>APDW18::N0TEST   :Hello there{12",
        "N0ABC>APDW18:>Status text here",
    };

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [DireWolfFact]
    public async Task ProbeReportsVersion()
    {
        var v = await DireWolfProbe.ProbeAsync(Exe!);
        Assert.NotNull(v);
        Assert.Equal(1, v!.Major);
        Assert.True(v.Minor >= 7);
        Assert.StartsWith("Dire Wolf ", v.Banner.First(l => l.StartsWith("Dire Wolf ")));
    }

    [DireWolfFact]
    public async Task ReceiveMonitorRawKissTransmitCsvAndGracefulStop()
    {
        string dir = CreateWorkDir();
        int agwPort = FreePort(), kissPort = FreePort();
        string logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        string conf = WriteConfig(dir, agwPort, kissPort);
        var audio = GenerateAudio(dir, TestPackets);

        await using var controller = new DireWolfProcessController();
        var parser = new ConsoleOutputParser();
        var lines = new ConcurrentQueue<string>();
        var packets = new ConcurrentQueue<PacketRecord>();
        var notices = new ConcurrentQueue<ConsoleNotice>();
        var parseLock = new object();
        parser.PacketParsed += packets.Enqueue;
        parser.Notice += notices.Enqueue;
        controller.OutputLine += (_, e) => { lines.Enqueue(e.Line); lock (parseLock) parser.ProcessLine(e.Line, e.Time); };
        DireWolfExitedEventArgs? exited = null;
        controller.Exited += (_, e) => exited = e;
        try
        {
            await controller.StartAsync(new DireWolfLaunchOptions
            {
                ExecutablePath = Exe!, ConfigPath = conf, WorkingDirectory = dir, LogDirectory = logs, StandardInputAudio = true,
            });
            Assert.Equal(DireWolfState.Running, controller.State);
            await WaitUntil(() => notices.Any(n => n.Kind == ConsoleNoticeKind.ServerListening && n.Protocol == "KISS" && n.Port == kissPort)
                                  && notices.Any(n => n.Kind == ConsoleNoticeKind.ServerListening && n.Protocol == "AGW"), Wait, "server ports");
            Assert.Equal(agwPort, notices.First(n => n.Protocol == "AGW").Port);
            Assert.DoesNotContain(notices, n => n.Kind == ConsoleNoticeKind.ServerListening && n.Port == 8001);

            await using var agw = new AgwClient();
            var agwFrames = new ConcurrentQueue<AgwFrame>();
            agw.FrameReceived += agwFrames.Enqueue;
            await agw.ConnectAsync("127.0.0.1", agwPort);
            var (major, _) = await agw.RequestVersionAsync();
            Assert.Equal(2005, major);
            var ports = await agw.RequestPortInfoAsync();
            Assert.True(ports.Count >= 1);
            await agw.EnableMonitoringAsync();
            await agw.EnableRawFramesAsync();

            await using var kiss = new KissTcpClient();
            var kissFrames = new ConcurrentQueue<KissFrame>();
            kiss.FrameReceived += kissFrames.Enqueue;
            await kiss.ConnectAsync("127.0.0.1", kissPort);
            await Task.Delay(300);   // let Dire Wolf process the 'm'/'k' toggles and the KISS attach

            await controller.WriteStandardInputAsync(audio);
            await WaitUntil(() => packets.Count >= TestPackets.Length, Wait, "decoded packets");

            // Console parsing of real output.
            var rx = packets.ToArray();
            var pos = rx.First(p => p.Source == "WB2OSZ-15");
            Assert.Equal(PacketDirection.Received, pos.Direction);
            Assert.Equal(0, pos.Channel);
            Assert.Equal("APDW12", pos.Destination);
            Assert.Equal(new[] { "WIDE2-1" }, pos.Path);
            Assert.InRange(pos.AudioLevel!.Value, 1, 150);
            Assert.NotNull(pos.AudioLevelText);
            Assert.Equal(42.619, pos.Aprs!.Latitude!.Value, 3);
            await WaitUntil(() => pos.DecodedLines.Any(l => l.Contains("N 42 37.1400")), Wait, "decode lines");
            var micE = rx.First(p => p.Source == "N1ZKO-7");
            Assert.Equal(AprsPacketType.MicE, micE.Aprs!.Type);
            Assert.Equal(42 + 43.78 / 60, micE.Aprs.Latitude!.Value, 4);
            var msg = rx.First(p => p.Aprs?.Type == AprsPacketType.Message);
            Assert.Equal(("N0TEST", "12", "Hello there"), (msg.Aprs!.Addressee, msg.Aprs.MessageId, msg.Aprs.MessageText));

            // AGW monitor ('U') and raw ('K') frames for the same packets.
            await WaitUntil(() => agwFrames.Count(f => f.DataKind == 'K') >= TestPackets.Length && agwFrames.Count(f => f.DataKind == 'U') >= TestPackets.Length, Wait, "AGW U and K frames");
            var u = agwFrames.First(f => f.DataKind == 'U' && f.CallFrom == "WB2OSZ-15");
            Assert.Equal("APDW12", u.CallTo);
            Assert.Contains("Fm WB2OSZ-15 To APDW12 Via WIDE2-1", u.DataText);
            var raws = agwFrames.Where(f => f.DataKind == 'K').Select(f => { Assert.True(Ax25Frame.TryDecode(f.Payload.AsSpan(1), out var fr, out _)); return fr!; }).ToList();
            Assert.Contains(raws, f => f.ToMonitorString() == TestPackets[0]);
            Assert.Contains(raws, f => f.ToMonitorString() == TestPackets[3]);

            // KISS TCP gets the same frames.
            await WaitUntil(() => kissFrames.Count >= TestPackets.Length, Wait, "KISS frames");
            var kf = kissFrames.Select(k => { Assert.True(Ax25Frame.TryDecode(k.Data, out var f, out _)); return f!.ToMonitorString(); }).ToList();
            Assert.Contains(TestPackets[2], kf);

            // CSV log written by Dire Wolf (-l) contains the positions.
            var tailer = new DireWolfCsvLogTailer(logs, startAtEnd: false);
            var records = new List<DireWolfLogRecord>();
            await WaitUntil(() => { records.AddRange(tailer.Poll()); return records.Count(r => r.HasPosition) >= 2; }, Wait, "CSV log positions");
            var csvPos = records.First(r => r.Source == "WB2OSZ-15");
            Assert.Equal(42.619, csvPos.Latitude!.Value, 3);
            Assert.Equal(-71.347167, csvPos.Longitude!.Value, 4);
            var tracker = new StationTracker();
            foreach (var r in records) tracker.Update(r, countAsPacket: true);
            Assert.Equal(PositionSources.DireWolfLog, tracker.TryGet("N1ZKO-7")!.PositionSource);

            // Transmit through AGW unproto: Dire Wolf prints "[0L]"/"[0H]" and sends 'T' monitor frames.
            var sender = new AgwPacketSender(agw);
            await sender.SendUnprotoAsync(0, "N0TEST", "APDW18", new[] { "WIDE1-1" }, ">GUI test transmission");
            await WaitUntil(() => packets.Any(p => p.Direction == PacketDirection.Transmitted), Wait, "transmit line");
            var tx = packets.First(p => p.Direction == PacketDirection.Transmitted);
            Assert.Matches(@"^0[LH]$", tx.Label);
            Assert.Equal(("N0TEST", "APDW18", ">GUI test transmission"), (tx.Source, tx.Destination, tx.Info));
            await sender.SendUnprotoAsync(0, "N0TEST", "APDW18", Array.Empty<string>(), ">no path");   // 'M'
            await WaitUntil(() => agwFrames.Any(f => f.DataKind == 'T' && f.DataText.Contains("GUI test transmission")), Wait, "AGW 'T' frame");
            await WaitUntil(() => packets.Any(p => p.Direction == PacketDirection.Transmitted && p.Info == ">no path"), Wait, "'M' transmit");

            // Graceful stop with SIGINT / Ctrl+C.
            int pid = controller.ProcessId!.Value;
            var stop = await controller.StopAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(StopOutcome.Graceful, stop.Outcome);
            Assert.False(stop.PttMayBeKeyed);
            Assert.Equal(DireWolfState.Stopped, controller.State);
            Assert.Contains("QRT", lines);
            Assert.NotNull(exited);
            Assert.True(exited!.Expected);
            Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        }
        finally
        {
            await controller.StopAsync();
            TryDelete(dir);
        }
    }

    [DireWolfFact]
    public async Task StationSessionEndToEnd()
    {
        string dir = CreateWorkDir();
        int agwPort = FreePort(), kissPort = FreePort();
        string conf = WriteConfig(dir, agwPort, kissPort);
        var audio = GenerateAudio(dir, TestPackets);
        await using var session = new StationSession();
        try
        {
            await session.StartAsync(new StationSessionOptions
            {
                Launch = new DireWolfLaunchOptions { ExecutablePath = Exe!, ConfigPath = conf, WorkingDirectory = dir, LogDirectory = Path.Combine(dir, "logs"), StandardInputAudio = true },
                MyCall = "N0TEST",
            });
            await WaitUntil(() => session.AgwMonitorReady, Wait, "AGW monitor connection (port learned from output)");
            Assert.Equal(agwPort, session.AgwPort);
            Assert.Contains(kissPort, session.KissPorts);

            await session.Controller.WriteStandardInputAsync(audio);
            await WaitUntil(() => session.Packets.Snapshot().Count(p => p.Direction == PacketDirection.Received) >= TestPackets.Length, Wait, "packets in store");
            await WaitUntil(() => session.Packets.Snapshot().Count(p => p.RawFrame != null) >= TestPackets.Length, Wait, "raw frames correlated from AGW 'K'");
            var pos = session.Packets.Snapshot().First(p => p.Source == "WB2OSZ-15");
            Assert.True(Ax25Frame.TryDecode(pos.RawFrame, out var raw, out _));
            Assert.Equal(TestPackets[0], raw!.ToMonitorString());

            var st = session.Stations.TryGet("WB2OSZ-15")!;
            Assert.True(st.HasPosition);
            Assert.Equal(42.619, st.Latitude!.Value, 3);
            Assert.Equal("Status text here", session.Stations.TryGet("N0ABC")!.Status);
            var activity = Assert.Single(session.ChannelActivity);
            Assert.Equal(TestPackets.Length, activity.Received);
            Assert.True(session.LastAudioLevels.ContainsKey(0));

            // The message to N0TEST was received; nothing was transmitted because auto-ack is off by default.
            var inbound = Assert.Single(session.Messages.GetMessages());
            Assert.Equal(("N0ABC", "Hello there", AprsMessageState.Received), (inbound.From, inbound.Text, inbound.State));
            await Task.Delay(500);
            Assert.DoesNotContain(session.Packets.Snapshot(), p => p.Direction == PacketDirection.Transmitted);

            // Log buffer is filled and pollable.
            var log = session.Log.GetSince(0);
            Assert.Contains(log, e => e.Text.StartsWith("Dire Wolf "));
            Assert.Contains(log, e => e.Severity == DireWolfGui.Core.Logging.LogSeverity.Packet);
            Assert.True(session.Uptime > TimeSpan.Zero);
            Assert.NotNull(session.SampleResources());
            Assert.NotNull(session.DireWolfVersion);

            // CSV tail (directory taken from -l) gives Dire Wolf's decoded position for the Mic-E station too.
            await WaitUntil(() => session.Stations.TryGet("N1ZKO-7")?.PositionSource == PositionSources.DireWolfLog, Wait, "CSV positions in tracker");
            Assert.Equal(1, session.Stations.TryGet("N1ZKO-7")!.PacketCount);   // the log line did not count as a second packet

            var stop = await session.StopAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(StopOutcome.Graceful, stop.Outcome);
            Assert.False(session.AgwConnected);
        }
        finally
        {
            await session.StopAsync();
            TryDelete(dir);
        }
    }

    [DireWolfFact]
    public async Task BadConfigPathIsReportedWithDiagnosis()
    {
        string dir = CreateWorkDir();
        try
        {
            await using var controller = new DireWolfProcessController();
            var exited = new TaskCompletionSource<DireWolfExitedEventArgs>();
            controller.Exited += (_, e) => exited.TrySetResult(e);
            await controller.StartAsync(new DireWolfLaunchOptions { ExecutablePath = Exe!, ConfigPath = Path.Combine(dir, "missing.conf"), WorkingDirectory = dir });
            var e = await exited.Task.WaitAsync(Wait);
            Assert.False(e.Expected);
            Assert.Equal(1, e.ExitCode);
            Assert.Contains("configuration file could not be opened", e.Diagnosis!.Summary);
            Assert.Equal(DireWolfState.Failed, controller.State);
            Assert.Equal(e.Diagnosis, controller.LastDiagnosis);
        }
        finally { TryDelete(dir); }
    }

    [DireWolfFact]
    public async Task SessionReportsMissingExecutableAndStdinEof()
    {
        await using (var s = new StationSession())
        {
            await Assert.ThrowsAsync<DireWolfStartException>(() => s.StartAsync(new StationSessionOptions
            {
                Launch = new DireWolfLaunchOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-direwolf"), ConfigPath = "x.conf" },
            }));
            Assert.Contains(s.Alerts.Snapshot(), a => a.Text.Contains("not found"));
            Assert.Equal(DireWolfState.Failed, s.State);
        }

        string dir = CreateWorkDir();
        try
        {
            string conf = WriteConfig(dir, FreePort(), FreePort());
            await using var c = new DireWolfProcessController();
            var exited = new TaskCompletionSource<DireWolfExitedEventArgs>();
            c.Exited += (_, e) => exited.TrySetResult(e);
            await c.StartAsync(new DireWolfLaunchOptions { ExecutablePath = Exe!, ConfigPath = conf, WorkingDirectory = dir, StandardInputAudio = true });
            await Task.Delay(500);
            c.CloseStandardInput();
            var e = await exited.Task.WaitAsync(Wait);
            Assert.False(e.Expected);
            Assert.Contains("Audio input from standard input ended", e.Diagnosis!.Summary);
        }
        finally { TryDelete(dir); }
    }
}
