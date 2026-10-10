using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Tests.Config;

public class BackupProfileDiffTests
{
    [Fact]
    public void Backup_list_restore_prune()
    {
        using var t = new TempDir();
        string conf = t.File("direwolf.conf");
        var m = new ConfigBackupManager(t.File("backups"), keep: 3);
        Assert.Null(m.Backup(conf));
        Assert.Empty(m.List());

        for (int i = 1; i <= 5; i++)
        {
            File.WriteAllText(conf, $"version {i}\n");
            Assert.NotNull(m.Backup(conf));
        }
        var list = m.List("direwolf.conf");
        Assert.Equal(3, list.Count);                      // pruned to Keep
        Assert.Equal("version 5\n", File.ReadAllText(list[0].Path)); // newest first
        Assert.Equal("direwolf.conf", list[0].OriginalFileName);

        File.WriteAllText(conf, "current\n");
        string? safety = m.Restore(list[2].Path, conf);
        Assert.Equal("version 3\n", File.ReadAllText(conf));
        Assert.NotNull(safety);
        Assert.Equal("current\n", File.ReadAllText(safety!));
        Assert.Contains(m.List(), b => b.Path == safety);

        File.WriteAllText(t.File("other.conf"), "x");
        m.Backup(t.File("other.conf"));
        Assert.Single(m.List("other.conf"));
        Assert.Equal(1, m.Prune(null, 2));
        Assert.Equal(2, m.List("direwolf.conf").Count);
        Assert.Single(m.List("other.conf"));
    }

    [Theory]
    [InlineData("My Station", "My Station")]
    [InlineData("  a/b\\c:d*?  ", "a_b_c_d__")]
    [InlineData("home.conf", "home")]
    [InlineData("CON", "_CON")]
    [InlineData("..\\..\\evil", "_.._evil")]
    public void Profile_names_are_sanitized(string input, string expected) => Assert.Equal(expected, ProfileManager.SanitizeName(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void Unusable_profile_names_throw(string input) => Assert.Throws<ArgumentException>(() => ProfileManager.SanitizeName(input));

    [Fact]
    public void Profiles_import_export_duplicate_rename_delete()
    {
        using var t = new TempDir();
        var pm = new ProfileManager(t.File("profiles"));
        Assert.Empty(pm.List());
        string src = t.File("source.conf");
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'A', (byte)'\r', (byte)'\n'];
        File.WriteAllBytes(src, bytes);

        pm.Import(src, "Home");
        Assert.Throws<IOException>(() => pm.Import(src, "Home"));
        Assert.Equal(bytes, File.ReadAllBytes(pm.PathFor("Home")));
        pm.Duplicate("Home", "Portable");
        Assert.Throws<IOException>(() => pm.Duplicate("Home", "Portable"));
        pm.Rename("Portable", "Field Day");
        pm.Rename("Field Day", "field day");
        Assert.Equal(["field day", "Home"], pm.List().Select(p => p.Name));
        pm.Export("Home", t.File("out.conf"));
        Assert.Equal(bytes, File.ReadAllBytes(t.File("out.conf")));
        Assert.Throws<IOException>(() => pm.Export("Home", t.File("out.conf")));

        var backups = new ConfigBackupManager(t.File("backups"));
        Assert.True(pm.Delete("Home", backups));
        Assert.False(pm.Delete("Home"));
        Assert.Single(backups.List());
        Assert.Throws<FileNotFoundException>(() => pm.Rename("Home", "x"));
        pm.Save("New", ConfigDocument.Parse("MYCALL K1ABC\n"));
        Assert.Equal("MYCALL K1ABC\n", File.ReadAllText(pm.PathFor("New")));
    }

    [Fact]
    public void Diff_finds_minimal_changes()
    {
        var d = TextDiff.Compute("a\nb\nc\nd\n", "a\nB\nc\nd\ne\n");
        Assert.Equal(["  a", "- b", "+ B", "  c", "  d", "+ e"], d.Select(x => (x.Kind switch { DiffKind.Added => "+ ", DiffKind.Removed => "- ", _ => "  " }) + x.Text));
        var removed = d.Single(x => x.Kind == DiffKind.Removed);
        Assert.Equal((2, 0), (removed.OldLineNumber, removed.NewLineNumber));
        Assert.Equal((0, 5), (d[^1].OldLineNumber, d[^1].NewLineNumber));
        Assert.True(TextDiff.HasChanges(d));
        Assert.False(TextDiff.HasChanges(TextDiff.Compute("x\r\ny", "x\ny\n")));
        Assert.Empty(TextDiff.Compute("", ""));
    }

    [Fact]
    public void Diff_handles_insertions_deletions_and_random_cases()
    {
        var rnd = new Random(42);
        for (int iter = 0; iter < 200; iter++)
        {
            var a = Enumerable.Range(0, rnd.Next(0, 15)).Select(_ => ((char)('a' + rnd.Next(4))).ToString()).ToList();
            var b = Enumerable.Range(0, rnd.Next(0, 15)).Select(_ => ((char)('a' + rnd.Next(4))).ToString()).ToList();
            var d = TextDiff.Compute(a, b);
            Assert.Equal(a, d.Where(x => x.Kind != DiffKind.Added).Select(x => x.Text));
            Assert.Equal(b, d.Where(x => x.Kind != DiffKind.Removed).Select(x => x.Text));
            // Minimality against an LCS computed by dynamic programming.
            var lcs = new int[a.Count + 1, b.Count + 1];
            for (int i = a.Count - 1; i >= 0; i--)
                for (int j = b.Count - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            Assert.Equal(lcs[0, 0], d.Count(x => x.Kind == DiffKind.Same));
        }
    }

    [Fact]
    public void Unified_output()
    {
        var u = TextDiff.ToUnified(TextDiff.Compute("1\n2\n3\n4\n5\n6\n7\n8\n9\n", "1\n2\n3\n4\nfive\n6\n7\n8\n9\n"), 1, "old", "new");
        Assert.Equal("--- old\n+++ new\n@@ -4,3 +4,3 @@\n 4\n-5\n+five\n 6\n", u.Replace("\r\n", "\n"));
        Assert.Equal("", TextDiff.ToUnified(TextDiff.Compute("a", "a")));
    }
}
