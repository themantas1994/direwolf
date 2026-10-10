using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Tests.Config;

public class ConfigTokenizerTests
{
    [Theory]
    [InlineData("MODEM 1200", new[] { "MODEM", "1200" })]
    [InlineData("  \tADEVICE\tplughw:1,0   plughw:2,0\r\n", new[] { "ADEVICE", "plughw:1,0", "plughw:2,0" })]
    [InlineData("ADEVICE \"USB Audio CODEC\" 3", new[] { "ADEVICE", "USB Audio CODEC", "3" })]
    [InlineData("PBEACON symbol=\"digi\" comment=\"a b\"", new[] { "PBEACON", "symbol=digi", "comment=a b" })]
    [InlineData("X \"say \"\"hi\"\"\" y", new[] { "X", "say \"hi\"", "y" })]
    [InlineData("X ab\"c d\"e", new[] { "X", "abc de" })]
    [InlineData("", new string[0])]
    [InlineData("   \t ", new string[0])]
    public void Split_matches_direwolf(string line, string[] expected) => Assert.Equal(expected, ConfigTokenizer.Split(line));

    [Fact]
    public void Empty_quoted_token_ends_the_line_like_split_returning_NULL()
        => Assert.Equal(["IGLOGIN"], ConfigTokenizer.Split("IGLOGIN \"\" 123"));

    [Fact]
    public void Positions_point_into_the_original_line()
    {
        var t = ConfigTokenizer.Tokenize("  MODEM  1200");
        Assert.Equal(2, t[0].Start);
        Assert.Equal(7, t[0].End);
        Assert.Equal(9, t[1].Start);
    }

    [Theory]
    [InlineData("# comment", true)]
    [InlineData("   #x", true)]
    [InlineData("*star", true)]
    [InlineData("\"#quoted\"", true)]
    [InlineData("", true)]
    [InlineData("MYCALL X", false)]
    public void Comment_detection(string line, bool comment) => Assert.Equal(comment, ConfigTokenizer.IsCommentOrBlank(line));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"x\"", "\"say \"\"x\"\"\"")]
    [InlineData("", "\"\"")]
    public void Quote_round_trips_through_split(string value, string quoted)
    {
        Assert.Equal(quoted, ConfigTokenizer.Quote(value));
        if (value.Length > 0) Assert.Equal(["K", value], ConfigTokenizer.Split("K " + ConfigTokenizer.Quote(value)));
    }

    [Fact]
    public void Rest_of_line_keeps_spaces()
        => Assert.Equal("Message  received. ok", ConfigTokenizer.RestOfLine("TTERR OK SPEECH Message  \"received.\" ok", 3));
}
