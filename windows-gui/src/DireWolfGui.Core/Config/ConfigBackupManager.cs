using System.Globalization;
using System.Text.RegularExpressions;

namespace DireWolfGui.Core.Config;

public sealed record ConfigBackup(string Path, string OriginalFileName, DateTime TimestampUtc, long Size);

/// <summary>
/// Timestamped copies of configuration files in a backups directory, named
/// <c>&lt;name&gt;.&lt;yyyyMMdd-HHmmss-fff&gt;.bak</c> (UTC).  Keeps at most <see cref="Keep"/> per file.
/// </summary>
public sealed partial class ConfigBackupManager(string backupDirectory, int keep = 30)
{
    public string BackupDirectory { get; } = backupDirectory;
    public int Keep { get; set; } = keep;

    private const string Stamp = "yyyyMMdd-HHmmss-fff";

    [GeneratedRegex(@"^(?<name>.+)\.(?<ts>\d{8}-\d{6}-\d{3})(?:-(?<n>\d+))?\.bak$")]
    private static partial Regex BackupName();

    /// <summary>Copy the file into the backups directory; returns the backup path (null if the file does not exist).</summary>
    public string? Backup(string configPath)
    {
        if (!File.Exists(configPath)) return null;
        Directory.CreateDirectory(BackupDirectory);
        string name = Path.GetFileName(configPath);
        string ts = DateTime.UtcNow.ToString(Stamp, CultureInfo.InvariantCulture);
        string dest = Path.Combine(BackupDirectory, $"{name}.{ts}.bak");
        for (int n = 1; File.Exists(dest); n++) dest = Path.Combine(BackupDirectory, $"{name}.{ts}-{n}.bak");
        File.Copy(configPath, dest);
        Prune(name, Keep);
        return dest;
    }

    /// <summary>Backups, newest first; optionally only those of one file name.</summary>
    public IReadOnlyList<ConfigBackup> List(string? configFileName = null)
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        string? only = configFileName == null ? null : Path.GetFileName(configFileName);
        var list = new List<ConfigBackup>();
        foreach (var f in Directory.EnumerateFiles(BackupDirectory, "*.bak"))
        {
            var m = BackupName().Match(Path.GetFileName(f));
            if (!m.Success) continue;
            string orig = m.Groups["name"].Value;
            if (only != null && !string.Equals(orig, only, StringComparison.OrdinalIgnoreCase)) continue;
            var ts = DateTime.ParseExact(m.Groups["ts"].Value, Stamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            list.Add(new ConfigBackup(f, orig, ts, new FileInfo(f).Length));
        }
        return list.OrderByDescending(b => b.TimestampUtc).ThenByDescending(b => b.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Restore a backup over <paramref name="targetPath"/>; the current target is backed up first.
    /// Returns the backup made of the current file (null if there was none).</summary>
    public string? Restore(string backupPath, string targetPath)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Backup not found.", backupPath);
        var bytes = File.ReadAllBytes(backupPath);
        string? safety = Backup(targetPath);
        AtomicFile.WriteAllBytes(targetPath, bytes);
        return safety;
    }

    /// <summary>Delete all but the newest <paramref name="keep"/> backups (per file name, or all files when null).
    /// Returns the number deleted.</summary>
    public int Prune(string? configFileName, int keep)
    {
        int deleted = 0;
        foreach (var group in List(configFileName).GroupBy(b => b.OriginalFileName, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var old in group.Skip(Math.Max(0, keep)))
            {
                try { File.Delete(old.Path); deleted++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return deleted;
    }
}
