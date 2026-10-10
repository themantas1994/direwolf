using System.Text;
using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Tests.Config;

public class ConfigDocumentTests
{
    public static TheoryData<string> Samples()
    {
        var d = new TheoryData<string>();
        foreach (var f in TestEnv.SampleConfigs()) d.Add(Path.GetRelativePath(TestEnv.RepoRoot, f));
        return d;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Repository_configs_round_trip_byte_for_byte(string relative)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestEnv.RepoRoot, relative));
        var doc = ConfigDocument.FromBytes(bytes);
        Assert.Equal(bytes, doc.ToBytes());
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void There_are_samples() => Assert.True(TestEnv.SampleConfigs().Count() >= 4);

    [Theory]
    [InlineData("A 1\r\nB 2\r\n")]
    [InlineData("A 1\nB 2")]
    [InlineData("A 1\r\nB 2\nC 3\rD 4")]
    [InlineData("\n\n\r\n")]
    [InlineData("")]
    [InlineData("# only comment")]
    [InlineData("MYCALL X\r\n\r\n   \t\r\n")]
    public void Synthetic_texts_round_trip(string text)
    {
        var doc = ConfigDocument.Parse(text);
        Assert.Equal(text, doc.ToText());
        Assert.Equal(Encoding.UTF8.GetBytes(text), doc.ToBytes());
    }

    [Fact]
    public void Bom_is_kept_and_not_added()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("MYCALL N0CALL\r\n")];
        var doc = ConfigDocument.FromBytes(withBom);
        Assert.True(doc.HasBom);
        Assert.Equal("MYCALL", doc[0].Keyword);
        Assert.Equal(withBom, doc.ToBytes());
        doc.SetDirective("MYCALL", "K1ABC");
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("MYCALL K1ABC\r\n")], doc.ToBytes());

        var noBom = ConfigDocument.FromBytes(Encoding.UTF8.GetBytes("MYCALL X\n"));
        Assert.False(noBom.HasBom);
        Assert.Equal((byte)'M', noBom.ToBytes()[0]);
    }

    [Fact]
    public void Invalid_utf8_bytes_survive()
    {
        byte[] latin = [.. Encoding.ASCII.GetBytes("# caf"), 0xE9, (byte)'\n', .. Encoding.ASCII.GetBytes("MYCALL X\n")];
        var doc = ConfigDocument.FromBytes(latin);
        Assert.Equal(latin, doc.ToBytes());
        doc.SetDirective("MYCALL", "Y");
        Assert.Equal(0xE9, doc.ToBytes()[5]);
    }

    [Fact]
    public void Lines_have_kind_keyword_tokens_and_contexts()
    {
        var doc = ConfigDocument.Parse("""
            # header
            ADEVICE a b
            ACHANNELS 2
            ADEVICE1 c d
            ARATE 48000
            mycall n0call
            CHANNEL 1
            MODEM 9600

            CHANNEL 2
            PTT COM3 RTS
            BOGUS x
            """.Replace("\r\n", "\n"));
        Assert.Equal(ConfigLineKind.Comment, doc[0].Kind);
        Assert.Equal("ADEVICE", doc[1].Directive);
        Assert.Equal(0, doc[1].AudioDeviceDefined);
        Assert.Equal(1, doc[3].AudioDeviceDefined);
        Assert.Equal("ADEVICE1", doc[3].Keyword);
        Assert.Equal(1, doc[4].AudioDevice);
        Assert.Equal("MYCALL", doc[5].Keyword);
        Assert.Equal(0, doc[5].Channel);
        Assert.Equal(1, doc[7].Channel);
        Assert.Equal(["9600"], doc[7].Arguments);
        Assert.Equal(ConfigLineKind.Blank, doc[8].Kind);
        Assert.Equal(2, doc[10].Channel);
        Assert.False(doc[11].IsKnownDirective);
        Assert.Equal("BOGUS", doc[11].Directive);
    }

    [Fact]
    public void SetDirective_replaces_in_place_and_keeps_everything_else()
    {
        const string text = "# my station\r\nMYCALL N0CALL   # inline\r\n\r\nUNKNOWNTHING 1 2\r\n  MODEM 1200\r\n# end\r\n";
        var doc = ConfigDocument.Parse(text);
        int i = doc.SetDirective("MYCALL", "K1ABC-7");
        Assert.Equal(1, i);
        doc.SetDirective("MODEM", "9600");
        Assert.True(doc.IsDirty);
        Assert.Equal("# my station\r\nMYCALL K1ABC-7   # inline\r\n\r\nUNKNOWNTHING 1 2\r\n  MODEM 9600\r\n# end\r\n", doc.ToText());
    }

    [Fact]
    public void SetDirective_is_channel_aware()
    {
        var doc = ConfigDocument.Parse("ADEVICE a\nACHANNELS 2\nCHANNEL 0\nMYCALL A\nMODEM 1200\nCHANNEL 1\nMYCALL B\nMODEM 1200\nAGWPORT 8000\n");
        doc.SetDirective("MODEM", ["9600"], 1);
        doc.SetDirective("TXDELAY", ["40"], 0);
        doc.SetDirective("TXDELAY", ["50"], 1);
        Assert.Equal("ADEVICE a\nACHANNELS 2\nCHANNEL 0\nMYCALL A\nMODEM 1200\nTXDELAY 40\nCHANNEL 1\nMYCALL B\nMODEM 9600\nTXDELAY 50\nAGWPORT 8000\n", doc.ToText());
    }

    [Fact]
    public void SetDirective_creates_a_channel_section_when_needed()
    {
        var doc = ConfigDocument.Parse("ADEVICE a\nACHANNELS 2\nCHANNEL 0\nMODEM 1200\n\nAGWPORT 8000\n");
        doc.SetDirective("MODEM", ["WAPR", "H150"], 1);
        Assert.Equal("ADEVICE a\nACHANNELS 2\nCHANNEL 0\nMODEM 1200\nCHANNEL 1\nMODEM WAPR H150\n\nAGWPORT 8000\n", doc.ToText());
        Assert.Equal(1, doc.FindDirective("MODEM", 1)!.Channel);
    }

    [Fact]
    public void SetDirective_inserts_globals_before_first_channel_and_its_comment_block()
    {
        var doc = ConfigDocument.Parse("ADEVICE a\n\n# channel zero\nCHANNEL 0\nMODEM 1200\n");
        doc.SetDirective("KISSPORT", "8001");
        doc.SetDirective("AGWPORT", "8000");
        Assert.Equal("ADEVICE a\n\nKISSPORT 8001\nAGWPORT 8000\n# channel zero\nCHANNEL 0\nMODEM 1200\n", doc.ToText());
    }

    [Fact]
    public void SetDirective_implicit_channel_zero_and_audio_device()
    {
        var doc = ConfigDocument.Parse("ADEVICE a b\nMYCALL X\nAGWPORT 8000");
        doc.SetDirective("MODEM", "1200");
        doc.SetDirective("ACHANNELS", ["2"], 0);
        doc.SetDirective("ADEVICE1", "c", "d");
        Assert.Equal("ADEVICE a b\nACHANNELS 2\nADEVICE1 c d\nMYCALL X\nMODEM 1200\nAGWPORT 8000", doc.ToText());
        Assert.False(doc.EndsWithNewLine);
    }

    [Fact]
    public void Appending_keeps_missing_final_newline_and_line_ending_style()
    {
        var doc = ConfigDocument.Parse("A 1\r\nB 2");
        doc.InsertLine(doc.Count, "C 3");
        Assert.Equal("A 1\r\nB 2\r\nC 3", doc.ToText());
        doc.RemoveLine(2);
        Assert.Equal("A 1\r\nB 2", doc.ToText());
    }

    [Fact]
    public void Disable_comments_out_and_enable_restores()
    {
        var doc = ConfigDocument.Parse("DIGIPEAT 0 0 a b\nIGTXVIA 0 WIDE1-1\n");
        Assert.Equal(1, doc.DisableDirective("IGTXVIA", reason: "no internet to RF"));
        Assert.Equal(ConfigLineKind.Comment, doc[1].Kind);
        Assert.StartsWith(ConfigDocument.DisabledMarker + "IGTXVIA 0 WIDE1-1", doc[1].Text);
        Assert.Empty(doc.FindDirectives("IGTXVIA"));
        Assert.True(doc.EnableDirective(1));
        Assert.Equal("DIGIPEAT 0 0 a b\nIGTXVIA 0 WIDE1-1\n", doc.ToText());
        Assert.False(doc.EnableDirective(0));
    }

    [Fact]
    public void Replace_insert_remove_reject_line_breaks()
    {
        var doc = ConfigDocument.Parse("A\n");
        Assert.Throws<ArgumentException>(() => doc.ReplaceLine(0, "x\ny"));
        Assert.Throws<ArgumentException>(() => doc.InsertLine(0, "x\r"));
    }

    [Fact]
    public void Aliases_are_found()
    {
        var doc = ConfigDocument.Parse("DIGIPEATER 0 0 a b\nSMARTBEACONING\n");
        Assert.Single(doc.FindDirectives("DIGIPEAT"));
        Assert.Equal("SMARTBEACON", doc[1].Directive);
    }

    [Fact]
    public void Save_is_atomic_backs_up_and_preserves_bytes()
    {
        using var tmp = new TempDir();
        string path = tmp.File("direwolf.conf");
        File.WriteAllText(path, "MYCALL N0CALL\r\n# keep\r\n");
        var backups = new ConfigBackupManager(tmp.File("backups"));
        var doc = ConfigDocument.Load(path);
        doc.SetDirective("MYCALL", "K1ABC");
        doc.Save(backups: backups);
        Assert.False(doc.IsDirty);
        Assert.Equal("MYCALL K1ABC\r\n# keep\r\n", File.ReadAllText(path));
        var list = backups.List("direwolf.conf");
        Assert.Single(list);
        Assert.Equal("MYCALL N0CALL\r\n# keep\r\n", File.ReadAllText(list[0].Path));
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));
        string newFile = tmp.File("sub/new.conf");
        ConfigDocument.Parse("A\n").Save(newFile);
        Assert.Equal("A\n", File.ReadAllText(newFile));
    }
}
