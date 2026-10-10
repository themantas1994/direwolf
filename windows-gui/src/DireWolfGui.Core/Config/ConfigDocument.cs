using System.Text;

namespace DireWolfGui.Core.Config;

public enum ConfigLineKind { Blank, Comment, Directive }

/// <summary>
/// One physical line of a configuration file with what the real parser would make of it.
/// <see cref="Text"/> excludes the line ending, which is kept separately in <see cref="Ending"/>.
/// </summary>
public sealed class ConfigLine
{
    internal ConfigLine(int index, string text, string ending, int channel, int audioDevice)
    {
        Index = index;
        Text = text;
        Ending = ending;
        var tokens = ConfigTokenizer.Tokenize(text);
        Kind = tokens.Count == 0 ? ConfigLineKind.Blank
            : tokens[0].Value[0] is '#' or '*' ? ConfigLineKind.Comment : ConfigLineKind.Directive;
        if (Kind == ConfigLineKind.Directive)
        {
            Keyword = tokens[0].Value.ToUpperInvariant();
            Info = DirectiveCatalog.Find(Keyword);
            // A '#' token after the keyword is NOT a comment for Dire Wolf, but people write them;
            // keep it separate so edits can preserve it and the validator can warn about it.
            int hash = tokens.FindIndex(1, t => t.Value.StartsWith('#'));
            var args = hash < 0 ? tokens.Skip(1) : tokens.Skip(1).Take(hash - 1);
            Arguments = args.Select(t => t.Value).ToList();
            if (hash >= 0)
            {
                int start = tokens[hash].Start;
                while (start > 0 && text[start - 1] is ' ' or '\t') start--;
                TrailingComment = text[start..];
            }
            AllTokens = tokens.Skip(1).Select(t => t.Value).ToList();
        }
        Channel = channel;
        AudioDevice = audioDevice;
    }

    /// <summary>0-based index in the document; <see cref="LineNumber"/> is what Dire Wolf reports.</summary>
    public int Index { get; }
    public int LineNumber => Index + 1;
    public string Text { get; }
    /// <summary>"\r\n", "\n", "\r" or "" (last line without a line ending).</summary>
    public string Ending { get; }
    public ConfigLineKind Kind { get; }
    /// <summary>Upper-cased first token for directives (e.g. "ADEVICE1"), otherwise null.</summary>
    public string? Keyword { get; }
    /// <summary>Catalog entry, null for unknown keywords.</summary>
    public DirectiveInfo? Info { get; }
    /// <summary>Canonical directive name (alias resolved), or the keyword when unknown.</summary>
    public string? Directive => Info?.Name ?? Keyword;
    /// <summary>Arguments up to an inline "# comment" (if any).</summary>
    public IReadOnlyList<string> Arguments { get; } = [];
    /// <summary>All tokens after the keyword exactly as the real parser sees them (including any '#...').</summary>
    public IReadOnlyList<string> AllTokens { get; } = [];
    /// <summary>Text from an inline '#' token to the end, with the whitespace before it.</summary>
    public string? TrailingComment { get; }
    /// <summary>Radio channel in effect (latest CHANNEL line, 0 before any).</summary>
    public int Channel { get; }
    /// <summary>Audio device in effect (latest ADEVICEn / PAIDEVICE / PAODEVICE line, 0 before any).</summary>
    public int AudioDevice { get; }
    public bool IsDirective => Kind == ConfigLineKind.Directive;
    public bool IsKnownDirective => Info != null;
    /// <summary>For ADEVICEn lines, n; otherwise null.</summary>
    public int? AudioDeviceDefined => Info?.Name == "ADEVICE" ? ParseDeviceNumber(Keyword!) : null;

    internal static int ParseDeviceNumber(string keyword)
    {
        // atoi(t+7) like the real parser.
        int n = 0, i = 7;
        while (i < keyword.Length && char.IsAsciiDigit(keyword[i])) n = n * 10 + (keyword[i++] - '0');
        return n;
    }

    public override string ToString() => $"{LineNumber}: {Text}";
}

/// <summary>
/// Lossless model of a Dire Wolf configuration file.  Unedited, <see cref="ToText"/> and
/// <see cref="ToBytes"/> reproduce the input exactly: comments, blank lines, order, unknown
/// directives, each line's ending, the final newline (or its absence), the UTF-8 BOM if present.
/// Files that are not valid UTF-8 are read and written as Latin-1 so no byte changes.
/// </summary>
public sealed class ConfigDocument
{
    /// <summary>Prefix written by <see cref="DisableDirective(int, string?)"/>.</summary>
    public const string DisabledMarker = "#[disabled by Dire Wolf Station] ";

    private readonly List<(string Text, string Ending)> _raw = [];
    private List<ConfigLine>? _lines;

    public ConfigDocument() : this("", false) { }

    private ConfigDocument(string text, bool bom, Encoding? encoding = null)
    {
        HasBom = bom;
        Encoding = encoding ?? new UTF8Encoding(false);
        int i = 0, start = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                string ending = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : c.ToString();
                _raw.Add((text[start..i], ending));
                i += ending.Length;
                start = i;
            }
            else i++;
        }
        if (start < text.Length) _raw.Add((text[start..], ""));
        NewLine = _raw.GroupBy(r => r.Ending).Where(g => g.Key != "").OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
                  ?? (OperatingSystem.IsWindows() ? "\r\n" : "\n");
    }

    /// <summary>Parse text (BOM, if any, already removed; use <see cref="FromBytes"/> to keep one).</summary>
    public static ConfigDocument Parse(string text) => new(text, false);

    public static ConfigDocument FromBytes(byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = bom ? bytes.AsSpan(3) : bytes.AsSpan();
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return new ConfigDocument(strict.GetString(body), bom, new UTF8Encoding(false));
        }
        catch (DecoderFallbackException)
        {
            return new ConfigDocument(Encoding.Latin1.GetString(body), bom, Encoding.Latin1);
        }
    }

    public static ConfigDocument Load(string path)
    {
        var doc = FromBytes(File.ReadAllBytes(path));
        doc.FilePath = path;
        return doc;
    }

    /// <summary>File this document was loaded from or last saved to.</summary>
    public string? FilePath { get; private set; }
    public bool HasBom { get; }
    /// <summary>UTF-8 (no BOM in the encoding; see <see cref="HasBom"/>) or Latin-1 for non UTF-8 files.</summary>
    public Encoding Encoding { get; }
    /// <summary>Line ending used for inserted lines: the most common one in the file.</summary>
    public string NewLine { get; set; }
    public bool IsDirty { get; private set; }
    /// <summary>True if the last line ends with a line ending (or the document is empty).</summary>
    public bool EndsWithNewLine => _raw.Count == 0 || _raw[^1].Ending != "";

    public IReadOnlyList<ConfigLine> Lines => _lines ??= BuildLines();
    public int Count => _raw.Count;
    public ConfigLine this[int index] => Lines[index];

    private List<ConfigLine> BuildLines()
    {
        var list = new List<ConfigLine>(_raw.Count);
        int channel = 0, adev = 0;
        for (int i = 0; i < _raw.Count; i++)
        {
            // Context changes take effect for the line itself, as in config_init().
            var tokens = ConfigTokenizer.Split(_raw[i].Text);
            if (tokens.Count > 0 && tokens[0][0] is not ('#' or '*'))
            {
                string k = tokens[0].ToUpperInvariant();
                if (k == "CHANNEL" && tokens.Count > 1 && int.TryParse(tokens[1], out int ch) && ch >= 0 && ch < 6) channel = ch;
                else if (k.StartsWith("ADEVICE", StringComparison.Ordinal)) adev = ConfigLine.ParseDeviceNumber(k);
                else if (k is "PAIDEVICE" or "PAODEVICE") adev = 0;
            }
            list.Add(new ConfigLine(i, _raw[i].Text, _raw[i].Ending, channel, adev));
        }
        return list;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var (text, ending) in _raw) sb.Append(text).Append(ending);
        return sb.ToString();
    }

    public byte[] ToBytes()
    {
        var body = Encoding.GetBytes(ToText());
        return HasBom ? [0xEF, 0xBB, 0xBF, .. body] : body;
    }

    public override string ToString() => ToText();

    // ------------------------------------------------------------------ queries

    public IEnumerable<ConfigLine> Directives => Lines.Where(l => l.IsDirective);

    /// <summary>Directive lines for a keyword (aliases included).  For channel scoped directives
    /// <paramref name="context"/> is the channel, for audio device ones the device number.</summary>
    public IEnumerable<ConfigLine> FindDirectives(string keyword, int? context = null)
    {
        var info = DirectiveCatalog.Find(keyword);
        string name = info?.Name ?? keyword.ToUpperInvariant();
        return Directives.Where(l => l.Directive == name && (context is null || ContextOf(l, info) == context));
    }

    private static int? ContextOf(ConfigLine l, DirectiveInfo? info) => info?.Scope switch
    {
        DirectiveScope.Channel => l.Channel,
        DirectiveScope.AudioDevice => l.AudioDeviceDefined ?? l.AudioDevice,
        _ => null,
    };

    /// <summary>The last matching directive (the one Dire Wolf ends up using for most settings).</summary>
    public ConfigLine? FindDirective(string keyword, int? context = null) => FindDirectives(keyword, context).LastOrDefault();

    // ------------------------------------------------------------------ edits

    private void Changed() { _lines = null; IsDirty = true; }

    public void MarkClean() => IsDirty = false;

    public void ReplaceLine(int index, string text)
    {
        CheckSingleLine(text);
        _raw[index] = (text, _raw[index].Ending);
        Changed();
    }

    /// <summary>Insert a line before <paramref name="index"/> (index == Count appends).</summary>
    public void InsertLine(int index, string text)
    {
        CheckSingleLine(text);
        if (index < 0 || index > _raw.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _raw.Count && _raw.Count > 0 && _raw[^1].Ending == "")
        {
            // Keep "no newline at end of file": the old last line gets one, the new line doesn't.
            _raw[^1] = (_raw[^1].Text, NewLine);
            _raw.Add((text, ""));
        }
        else _raw.Insert(index, (text, NewLine));
        Changed();
    }

    public void InsertLines(int index, IEnumerable<string> lines)
    {
        foreach (var l in lines) InsertLine(index++, l);
    }

    public void RemoveLine(int index)
    {
        bool wasLast = index == _raw.Count - 1;
        string ending = _raw[index].Ending;
        _raw.RemoveAt(index);
        if (wasLast && ending == "" && _raw.Count > 0) _raw[^1] = (_raw[^1].Text, "");
        Changed();
    }

    /// <summary>Comment out a directive with <see cref="DisabledMarker"/> (never deletes it).</summary>
    public void DisableDirective(int index, string? reason = null)
    {
        var l = Lines[index];
        if (!l.IsDirective) return;
        ReplaceLine(index, DisabledMarker + l.Text + (string.IsNullOrWhiteSpace(reason) ? "" : "   # " + reason.Trim()));
    }

    /// <summary>Disable all matching directives; returns how many lines were changed.</summary>
    public int DisableDirective(string keyword, int? context = null, string? reason = null)
    {
        var idx = FindDirectives(keyword, context).Select(l => l.Index).ToList();
        foreach (var i in idx) DisableDirective(i, reason);
        return idx.Count;
    }

    /// <summary>Undo <see cref="DisableDirective(int, string?)"/>.  Returns false if the line was not disabled by us.</summary>
    public bool EnableDirective(int index)
    {
        string t = _raw[index].Text;
        if (!t.StartsWith(DisabledMarker, StringComparison.Ordinal)) return false;
        t = t[DisabledMarker.Length..];
        int reason = t.LastIndexOf("   # ", StringComparison.Ordinal);
        if (reason >= 0) t = t[..reason];
        ReplaceLine(index, t);
        return true;
    }

    /// <summary>
    /// Set a directive: replace the last existing line for it (in the given channel / audio device
    /// context) in place, keeping its indentation and any inline '#' text, or insert a new line in
    /// the right section.  <paramref name="args"/> are written verbatim, separated by one space
    /// (use <see cref="ConfigTokenizer.Quote"/> for values with spaces).  Returns the line index.
    /// </summary>
    public int SetDirective(string keyword, IEnumerable<string> args, int? context = null)
    {
        var info = DirectiveCatalog.Find(keyword);
        int ctx = context ?? (info?.Name == "ADEVICE" ? ConfigLine.ParseDeviceNumber(keyword.ToUpperInvariant()) : 0);
        int? scopeCtx = info?.Scope is DirectiveScope.Channel or DirectiveScope.AudioDevice ? ctx : null;
        string body = string.Join(" ", new[] { keyword }.Concat(args).Where(s => s.Length > 0));
        if (info?.Name == "ADEVICE") body = string.Join(" ", new[] { ctx == 0 ? keyword : $"ADEVICE{ctx}" }.Concat(args));

        var existing = FindDirective(keyword, scopeCtx);
        if (existing != null)
        {
            string indent = existing.Text[..(existing.Text.Length - existing.Text.TrimStart(' ', '\t').Length)];
            ReplaceLine(existing.Index, indent + body + (existing.TrailingComment ?? ""));
            return existing.Index;
        }
        int at = InsertionPoint(info, ctx, out string? header);
        if (header != null)
        {
            InsertLine(at, header);
            at++;
        }
        InsertLine(at, body);
        return at;
    }

    public int SetDirective(string keyword, params string[] args) => SetDirective(keyword, args, null);

    private int InsertionPoint(DirectiveInfo? info, int ctx, out string? header)
    {
        header = null;
        var lines = Lines;
        int firstChannel = lines.FirstOrDefault(l => l.Directive == "CHANNEL")?.Index ?? -1;
        int AfterLastOf(Func<ConfigLine, bool> pred, int fallback)
        {
            var last = lines.LastOrDefault(pred);
            return last != null ? last.Index + 1 : fallback;
        }
        int BeforeCommentBlock(int index)
        {
            while (index > 0 && lines[index - 1].Kind == ConfigLineKind.Comment) index--;
            return index;
        }
        int globalsEnd = firstChannel >= 0 ? BeforeCommentBlock(firstChannel) : lines.Count;

        switch (info?.Scope)
        {
            case DirectiveScope.Channel:
            {
                var chHeader = lines.LastOrDefault(l => l.Directive == "CHANNEL" && l.Channel == ctx);
                if (chHeader != null || (ctx == 0 && firstChannel < 0))
                    return AfterLastOf(l => l.Channel == ctx && l.Info?.Scope == DirectiveScope.Channel && (chHeader == null || l.Index > chHeader.Index),
                        chHeader != null ? chHeader.Index + 1 : AfterLastOf(l => l.Info?.Scope == DirectiveScope.AudioDevice, lines.Count));
                if (ctx == 0)
                {
                    // Lines before the first CHANNEL line are channel 0.
                    int at = AfterLastOf(l => l.Index < firstChannel && l.Info?.Scope == DirectiveScope.Channel, -1);
                    return at >= 0 ? at : globalsEnd;
                }
                header = $"CHANNEL {ctx}";
                // After the section of the highest lower channel, else at the end.
                var lower = lines.Where(l => l.Directive == "CHANNEL" && l.Channel < ctx).Select(l => l.Channel).DefaultIfEmpty(-1).Max();
                if (lower >= 0)
                    return AfterLastOf(l => l.Channel == lower && l.Info?.Scope == DirectiveScope.Channel, lines.Count);
                return lines.Count;
            }
            case DirectiveScope.AudioDevice:
            {
                if (info.Name == "ADEVICE")
                    return AfterLastOf(l => l.Info?.Scope == DirectiveScope.AudioDevice && l.Index < globalsEnd, Math.Min(FirstDirectiveIndex(), globalsEnd));
                var dev = lines.LastOrDefault(l => l.AudioDeviceDefined == ctx);
                if (dev != null) return AfterLastOf(l => l.Info?.Scope == DirectiveScope.AudioDevice && l.AudioDevice == ctx && l.Index >= dev.Index, dev.Index + 1);
                return AfterLastOf(l => l.Info?.Scope == DirectiveScope.AudioDevice && l.AudioDevice == ctx, Math.Min(FirstDirectiveIndex(), globalsEnd));
            }
            default:
            {
                // Next to other directives of the same category, else before the first CHANNEL line, else at the end.
                if (info != null)
                {
                    var same = lines.LastOrDefault(l => l.Info?.Category == info.Category && l.Info.Scope == DirectiveScope.Global);
                    if (same != null) return same.Index + 1;
                }
                return globalsEnd;
            }
        }
    }

    private int FirstDirectiveIndex() => Lines.FirstOrDefault(l => l.IsDirective)?.Index ?? Lines.Count;

    private static void CheckSingleLine(string text)
    {
        if (text.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("A configuration line cannot contain a line break.", nameof(text));
    }

    // ------------------------------------------------------------------ saving

    /// <summary>
    /// Write atomically: a temporary file in the same directory, then File.Replace (or Move for a new
    /// file).  If <paramref name="backups"/> is given and the target exists, it is backed up first.
    /// </summary>
    public void Save(string? path = null, ConfigBackupManager? backups = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No file path.");
        if (backups != null && File.Exists(path)) backups.Backup(path);
        AtomicFile.WriteAllBytes(path, ToBytes());
        FilePath = path;
        IsDirty = false;
    }
}

/// <summary>Atomic file replacement helper.</summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            if (File.Exists(full)) File.Replace(tmp, full, null, ignoreMetadataErrors: true);
            else File.Move(tmp, full);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
}
