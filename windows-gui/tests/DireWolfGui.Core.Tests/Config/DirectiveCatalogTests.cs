using System.Text.RegularExpressions;
using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Tests.Config;

/// <summary>
/// Compares <see cref="DirectiveCatalog"/> with the keywords the real parser accepts.
///
/// How keywords are found in src/config.c: config_init() is one long if / else-if chain on the
/// first token t, written at exactly one tab + two spaces of indentation ("\t  if (" / "\t  else if (").
/// Only those statements (including their continuation lines up to the opening brace) are top-level
/// keywords.  Comparisons of t nested deeper - sub-options such as ON/OFF, AUTO, NONE, rts/dtr,
/// GPIO/LPT/RIG/CM108, APRS/AX25, AIS/EAS/WAPR, G3RUH/V26A/V26B, PASSALL, DROP/MARK/TRACE/PREEMPT,
/// AIRTIME=, ATGP= - are excluded by that indentation rule, not by a word list.  SOFT_FIX is
/// a real top-level keyword and is therefore in the catalog.  strncasecmp(t, "ADEVICE", 7) is a prefix match.
/// </summary>
public class DirectiveCatalogTests
{
    /// <summary>Directives added to Dire Wolf after the config.c this branch is based on.
    /// Tolerated only while absent from config.c (to be removed after merging).</summary>
    private static readonly HashSet<string> AddedAfterThisSource = ["TCPBIND"];

    private static (HashSet<string> Exact, HashSet<string> Prefixes) ParserKeywords()
    {
        var lines = File.ReadAllLines(Path.Combine(TestEnv.RepoRoot, "src", "config.c"));
        int start = Array.FindIndex(lines, l => l.StartsWith("void config_init", StringComparison.Ordinal));
        int end = Array.FindIndex(lines, start, l => l.Contains("Unrecognized command", StringComparison.Ordinal));
        Assert.True(start > 0 && end > start, "config_init() not found in src/config.c");
        var top = new Regex(@"^\t  (else )?if \(");
        var cmp = new Regex(@"str(n?)casecmp\s*\(\s*t\s*,\s*""([^""]+)""");
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = start; i < end; i++)
        {
            if (!top.IsMatch(lines[i])) continue;
            var stmt = lines[i];
            for (int j = i; !lines[j].Contains('{') && j + 1 < end; j++) stmt += " " + lines[j + 1];
            foreach (Match m in cmp.Matches(stmt))
                (m.Groups[1].Value == "n" ? prefixes : exact).Add(m.Groups[2].Value.ToUpperInvariant());
        }
        return (exact, prefixes);
    }

    [Fact]
    public void Parser_keywords_were_found()
    {
        var (exact, prefixes) = ParserKeywords();
        Assert.True(exact.Count > 80, $"only {exact.Count} keywords found");
        Assert.Contains("ADEVICE", prefixes);
        Assert.Contains("WAPRGATE", exact);
        Assert.Contains("OBEACON", exact);       // continuation line of the PBEACON statement
        Assert.Contains("SMARTBEACONING", exact);
        Assert.DoesNotContain("PASSALL", exact);  // nested sub-option
        Assert.DoesNotContain("TRACE", exact);
    }

    [Fact]
    public void Catalog_covers_every_parser_keyword()
    {
        var (exact, prefixes) = ParserKeywords();
        var catalog = DirectiveCatalog.AllKeywords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = exact.Concat(prefixes).Where(k => !catalog.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "Missing from DirectiveCatalog: " + string.Join(", ", missing));
    }

    [Fact]
    public void Catalog_contains_only_parser_keywords()
    {
        var (exact, prefixes) = ParserKeywords();
        var extra = DirectiveCatalog.AllKeywords
            .Where(k => !exact.Contains(k) && !prefixes.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Where(k => !(AddedAfterThisSource.Contains(k) && !exact.Contains(k)))
            .ToList();
        Assert.True(extra.Count == 0, "In DirectiveCatalog but not accepted by src/config.c: " + string.Join(", ", extra));
    }

    [Fact]
    public void Entries_are_complete_and_unique()
    {
        Assert.Equal(DirectiveCatalog.AllKeywords.Count(), DirectiveCatalog.AllKeywords.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var d in DirectiveCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Summary), d.Name);
            Assert.False(string.IsNullOrWhiteSpace(d.Syntax), d.Name);
            Assert.False(string.IsNullOrWhiteSpace(d.Example), d.Name);
            Assert.True(d.RestartRequired);
            Assert.Equal(d.Name, d.Name.ToUpperInvariant());
        }
    }

    [Theory]
    [InlineData("mycall", "MYCALL", DirectiveScope.Channel)]
    [InlineData("ADEVICE2", "ADEVICE", DirectiveScope.AudioDevice)]
    [InlineData("serialkiss", "NULLMODEM", DirectiveScope.Global)]
    [InlineData("CDIGIPEATER", "CDIGIPEAT", DirectiveScope.Global)]
    [InlineData("TCPBIND", "TCPBIND", DirectiveScope.Global)]
    public void Find_resolves_aliases(string keyword, string name, DirectiveScope scope)
    {
        var d = DirectiveCatalog.Find(keyword)!;
        Assert.Equal(name, d.Name);
        Assert.Equal(scope, d.Scope);
    }

    [Fact]
    public void Flags_are_sensible()
    {
        Assert.True(DirectiveCatalog.Find("PBEACON")!.CanTransmit);
        Assert.True(DirectiveCatalog.Find("DIGIPEAT")!.ForwardsTraffic);
        Assert.True(DirectiveCatalog.Find("IGTXVIA")!.CanTransmit);
        Assert.True(DirectiveCatalog.Find("WAPRGATE")!.Experimental);
        Assert.False(DirectiveCatalog.Find("MYCALL")!.CanTransmit);
        Assert.Null(DirectiveCatalog.Find("BOGUS"));
    }
}
