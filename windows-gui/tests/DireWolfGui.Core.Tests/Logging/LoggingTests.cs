using DireWolfGui.Core.Logging;

namespace DireWolfGui.Core.Tests.Logging;

public class SequencedBufferTests
{
    private static LogEntry E(string t) => new(DateTimeOffset.UnixEpoch, LogSeverity.Info, t);

    [Fact]
    public void AssignsSequencesAndReturnsItemsSince()
    {
        var b = new SequencedBuffer<LogEntry>(10);
        for (int i = 0; i < 5; i++) b.Add(E("x" + i));
        Assert.Equal(5, b.LastSequence);
        var since = b.GetSince(2);
        Assert.Equal(new long[] { 3, 4, 5 }, since.Select(e => e.Sequence));
        Assert.Equal(2, b.GetSince(0, 2).Count);
        Assert.Empty(b.GetSince(5));
    }

    [Fact]
    public void DropsOldestWhenFullAndKeepsSequenceGrowing()
    {
        var b = new SequencedBuffer<LogEntry>(3);
        for (int i = 0; i < 7; i++) b.Add(E("x" + i));
        Assert.Equal(3, b.Count);
        Assert.Equal(5, b.FirstSequence);
        Assert.Equal(new[] { "x4", "x5", "x6" }, b.Snapshot().Select(e => e.Text));
        Assert.Equal(new[] { "x4", "x5", "x6" }, b.GetSince(1).Select(e => e.Text)); // poller fell behind: gets what is left
        Assert.Equal(new[] { "x5", "x6" }, b.GetLatest(2).Select(e => e.Text));
        b.Clear();
        Assert.Equal(0, b.Count);
        b.Add(E("y"));
        Assert.Equal(8, b.LastSequence);
        Assert.Single(b.GetSince(7));
    }

    [Fact]
    public async Task IsThreadSafe()
    {
        var b = new SequencedBuffer<LogEntry>(1000);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => { for (int i = 0; i < 500; i++) b.Add(E("t")); })));
        Assert.Equal(4000, b.LastSequence);
        var all = b.Snapshot();
        Assert.Equal(1000, all.Count);
        Assert.True(all.Zip(all.Skip(1)).All(p => p.Second.Sequence == p.First.Sequence + 1));
    }
}

public class SecretRedactorTests
{
    [Theory]
    [InlineData("IGLOGIN WB2OSZ-5 12345", "IGLOGIN WB2OSZ-5 *****")]
    [InlineData("  iglogin n0call -1", "  iglogin n0call *****")]
    [InlineData("user N0CALL pass 23456 vers DireWolf 1.8", "user N0CALL pass ***** vers DireWolf 1.8")]
    [InlineData("passcode=98765 other", "passcode=***** other")]
    [InlineData("Passcode: 11111", "Passcode: *****")]
    [InlineData("[0] N0ABC>APRS:>bypass road 12", "[0] N0ABC>APRS:>bypass road 12")]
    [InlineData("nothing secret here", "nothing secret here")]
    public void Masks(string input, string expected) => Assert.Equal(expected, SecretRedactor.Redact(input));
}

public class LogClassifierTests
{
    [Theory]
    [InlineData("[0.3] WB2OSZ-15>APDW12,WIDE2-1:!4237.14NS07120.83W#", LogSeverity.Packet)]
    [InlineData("[0 12:00:01] N0ABC>APRS:>hi", LogSeverity.Packet)]
    [InlineData("[0L] N0TEST>APDW18:>hello", LogSeverity.Transmit)]
    [InlineData("[ig>tx] N0A>APRS,qAR,X:>x", LogSeverity.Transmit)]
    [InlineData("[0>is] N0A>APRS:>x", LogSeverity.Transmit)]
    [InlineData("ERROR - Could not open configuration file nonexist.conf in cwd or homedir.", LogSeverity.Error)]
    [InlineData("Could not open audio device plughw:1,0 for input", LogSeverity.Error)]
    [InlineData("Line 12: Invalid channel number.", LogSeverity.Error)]
    [InlineData("Audio input level is too high. This may cause distortion and reduced decode performance.", LogSeverity.Warning)]
    [InlineData("Why are you running this as root user?.", LogSeverity.Warning)]
    [InlineData("[WAPR gate 1>0] not gated, duplicate: N0BBB", LogSeverity.Warning)]
    [InlineData("Ready to accept AGW client application 0 on port 8000 ...", LogSeverity.Info)]
    [InlineData("", LogSeverity.Debug)]
    public void Classifies(string line, LogSeverity expected) => Assert.Equal(expected, LogClassifier.Classify(line).Severity);

    [Fact]
    public void Categories()
    {
        Assert.Equal("agw", LogClassifier.Classify("Ready to accept AGW client application 0 on port 8000 ...").Category);
        Assert.Equal("kiss", LogClassifier.Classify("Ready to accept KISS TCP client application 0 on port 8001 ...").Category);
        Assert.Equal("audio", LogClassifier.Classify("ADEVICE0: Sample rate approx. 44.1 k, 0 errors, receive audio level CH0 73").Category);
        Assert.Equal("igate", LogClassifier.Classify("[ig] # logresp N0CALL verified").Category);
        Assert.Equal("wapr", LogClassifier.Classify("WAPR channel 1: N0BBB acknowledged frame 77.").Category);
    }
}

public class RotatingLogFileWriterTests
{
    [Fact]
    public void RotatesAndRedacts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dwlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var w = new RotatingLogFileWriter(Path.Combine(dir, "gui.log"), maxBytes: 2048, maxFiles: 3))
            {
                w.WriteLine("IGLOGIN N0CALL 12345");
                for (int i = 0; i < 200; i++) w.Write(new LogEntry(DateTimeOffset.UtcNow, LogSeverity.Info, $"line {i} " + new string('x', 40)));
                w.WriteLine("tail");
                Assert.True(File.Exists(w.RotatedPath(1)));
                Assert.True(File.Exists(w.RotatedPath(2)));
                Assert.False(File.Exists(w.RotatedPath(3)));
            }
            var all = string.Join("\n", Directory.GetFiles(dir).Select(File.ReadAllText));
            Assert.DoesNotContain("12345", all);
            Assert.Contains("tail", File.ReadAllText(Path.Combine(dir, "gui.log")));
            Assert.Contains("line 199", File.ReadAllText(Path.Combine(dir, "gui.log")) + File.ReadAllText(Path.Combine(dir, "gui.1.log")));
            Assert.All(Directory.GetFiles(dir), f => Assert.True(new FileInfo(f).Length < 2048 + 200));
        }
        finally { Directory.Delete(dir, true); }
    }
}
