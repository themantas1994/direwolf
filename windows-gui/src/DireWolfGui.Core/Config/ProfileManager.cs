using System.Text;

namespace DireWolfGui.Core.Config;

public sealed record ConfigProfile(string Name, string Path, DateTime LastWriteUtc, long Size);

/// <summary>
/// Named configuration files kept in a profiles directory as <c>&lt;name&gt;.conf</c>.
/// Names are sanitized so they are valid file names on Windows and elsewhere.
/// </summary>
public sealed class ProfileManager(string profilesDirectory)
{
    public const string Extension = ".conf";
    public string ProfilesDirectory { get; } = profilesDirectory;

    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Make a safe profile name: no path or reserved characters, no control characters,
    /// trimmed, at most 64 characters, not a Windows device name.  Throws if nothing is left.</summary>
    public static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name ?? "")
            sb.Append(char.IsControl(c) || Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim().TrimEnd('.').Trim();
        if (s.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) s = s[..^Extension.Length].TrimEnd('.', ' ');
        while (s.StartsWith('.')) s = s[1..];
        if (s.Length > 64) s = s[..64].TrimEnd();
        if (s.Length == 0 || s.Trim('_').Length == 0) throw new ArgumentException("The profile name is empty or has no usable characters.", nameof(name));
        if (Reserved.Contains(s) || Reserved.Contains(s.Split('.')[0])) s = "_" + s;
        return s;
    }

    public string PathFor(string name) => Path.Combine(ProfilesDirectory, SanitizeName(name) + Extension);

    public bool Exists(string name) => File.Exists(PathFor(name));

    public IReadOnlyList<ConfigProfile> List()
    {
        if (!Directory.Exists(ProfilesDirectory)) return [];
        return Directory.EnumerateFiles(ProfilesDirectory, "*" + Extension)
            .Select(f => new FileInfo(f))
            .Select(fi => new ConfigProfile(System.IO.Path.GetFileNameWithoutExtension(fi.Name), fi.FullName, fi.LastWriteTimeUtc, fi.Length))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Create or overwrite a profile with the given text (atomic write). Returns its path.</summary>
    public string Save(string name, ConfigDocument document)
    {
        string path = PathFor(name);
        Directory.CreateDirectory(ProfilesDirectory);
        AtomicFile.WriteAllBytes(path, document.ToBytes());
        return path;
    }

    /// <summary>Copy an existing configuration file in as a profile (byte for byte).</summary>
    public string Import(string sourcePath, string? name = null, bool overwrite = false)
    {
        string path = PathFor(name ?? System.IO.Path.GetFileNameWithoutExtension(sourcePath));
        if (File.Exists(path) && !overwrite) throw new IOException($"A profile named \"{System.IO.Path.GetFileNameWithoutExtension(path)}\" already exists.");
        Directory.CreateDirectory(ProfilesDirectory);
        AtomicFile.WriteAllBytes(path, File.ReadAllBytes(sourcePath));
        return path;
    }

    public void Export(string name, string destinationPath, bool overwrite = false)
    {
        string src = RequireExisting(name);
        if (File.Exists(destinationPath) && !overwrite) throw new IOException($"\"{destinationPath}\" already exists.");
        AtomicFile.WriteAllBytes(destinationPath, File.ReadAllBytes(src));
    }

    public string Duplicate(string name, string newName)
    {
        string src = RequireExisting(name);
        string dest = PathFor(newName);
        if (File.Exists(dest)) throw new IOException($"A profile named \"{SanitizeName(newName)}\" already exists.");
        File.Copy(src, dest);
        return dest;
    }

    public string Rename(string name, string newName)
    {
        string src = RequireExisting(name);
        string dest = PathFor(newName);
        if (string.Equals(src, dest, StringComparison.Ordinal)) return dest;
        bool caseOnly = string.Equals(src, dest, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(dest) && !caseOnly) throw new IOException($"A profile named \"{SanitizeName(newName)}\" already exists.");
        if (caseOnly)
        {
            string tmp = dest + ".rename";
            File.Move(src, tmp);
            File.Move(tmp, dest);
        }
        else File.Move(src, dest);
        return dest;
    }

    /// <summary>Delete a profile.  With a backup manager, a copy is kept there first.</summary>
    public bool Delete(string name, ConfigBackupManager? backups = null)
    {
        string path = PathFor(name);
        if (!File.Exists(path)) return false;
        backups?.Backup(path);
        File.Delete(path);
        return true;
    }

    private string RequireExisting(string name)
    {
        string p = PathFor(name);
        if (!File.Exists(p)) throw new FileNotFoundException($"No profile named \"{SanitizeName(name)}\".", p);
        return p;
    }
}
