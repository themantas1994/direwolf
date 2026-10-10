using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using DireWolfGui.Core.Config;
using DireWolfGui.Infrastructure;
using DireWolfGui.Views;

namespace DireWolfGui.ViewModels;

/// <summary>
/// The configuration document being edited, shared by the Configuration, Gateway &amp; digipeater
/// and WAPR pages so that they never hold different copies of the same file.  Edits are made
/// with ConfigDocument's targeted edits; saving always shows a diff preview, backs up the
/// current file and writes atomically.
/// </summary>
public sealed class ConfigurationSession : ObservableObject
{
    private static readonly ConditionalWeakTable<IShell, ConfigurationSession> Sessions = new();

    public static ConfigurationSession For(IShell shell) => Sessions.GetValue(shell, s => new ConfigurationSession(s));

    private readonly IShell _shell;
    private byte[] _savedBytes = [];
    private DateTime? _loadedWriteTimeUtc;
    private Task? _pending;

    private ConfigurationSession(IShell shell)
    {
        _shell = shell;
        _shell.StationStateChanged += (_, _) =>
        {
            if (!IsDirty && !SamePath(Path, _shell.ConfigPath)) EnsureCurrentAsync().Forget(_shell, "Loading the configuration");
            OnPropertyChanged(nameof(IsStale));
            OnPropertyChanged(nameof(StaleMessage));
        };
    }

    public ConfigDocument? Document { get; private set; }
    /// <summary>File the document was loaded from (and will be saved to).</summary>
    public string? Path { get; private set; }
    public bool HasDocument => Document != null;
    public string? LoadError { get; private set; }
    public bool IsLoading { get; private set; }

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    /// <summary>Incremented on every load and edit; pages compare it to know when to rebuild.</summary>
    public int Version { get; private set; }

    /// <summary>True when the editor holds unsaved changes to a file that is no longer the selected configuration.</summary>
    public bool IsStale => Document != null && !SamePath(Path, _shell.ConfigPath);

    public string StaleMessage => IsStale
        ? $"The editor holds unsaved changes to \"{Path}\", but the selected configuration is now \"{_shell.ConfigPath ?? "(none)"}\". Save them to \"{System.IO.Path.GetFileName(Path)}\" or discard them to load the selected file."
        : "";

    public string DisplayPath => Path ?? "(no configuration selected)";

    public event EventHandler? DocumentChanged;

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b);
        try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Make sure the document is the selected configuration: load it when another file was selected
    /// (unless there are unsaved edits; then <see cref="IsStale"/> is shown), and reload an unedited
    /// document when the file changed on disk.
    /// </summary>
    public Task EnsureCurrentAsync()
    {
        if (_pending is { IsCompleted: false }) return _pending;
        _pending = EnsureCoreAsync();
        return _pending;
    }

    private async Task EnsureCoreAsync()
    {
        string? want = _shell.ConfigPath;
        if (Document != null && SamePath(Path, want))
        {
            if (IsDirty || Path == null) return;
            DateTime? stamp = await Task.Run(() => File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : (DateTime?)null);
            if (stamp != _loadedWriteTimeUtc) await LoadAsync(Path);
            return;
        }
        if (IsDirty)
        {
            Notify();
            return;
        }
        if (string.IsNullOrWhiteSpace(want))
        {
            Clear();
            return;
        }
        await LoadAsync(want);
    }

    private void Clear()
    {
        Document = null;
        Path = null;
        LoadError = null;
        _savedBytes = [];
        IsDirty = false;
        Version++;
        Notify();
    }

    public async Task LoadAsync(string path)
    {
        IsLoading = true;
        OnPropertyChanged(nameof(IsLoading));
        try
        {
            var (doc, stamp, bytes) = await Task.Run(() =>
            {
                var d = ConfigDocument.Load(path);
                return (d, File.GetLastWriteTimeUtc(path), d.ToBytes());
            });
            Document = doc;
            Path = path;
            LoadError = null;
            _savedBytes = bytes;
            _loadedWriteTimeUtc = stamp;
            IsDirty = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Document = null;
            Path = path;
            LoadError = e is FileNotFoundException or DirectoryNotFoundException
                ? $"The configuration file \"{path}\" does not exist. Choose another file or create one with the Setup wizard."
                : $"The configuration file \"{path}\" could not be read: {e.Message}";
            _savedBytes = [];
            IsDirty = false;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsLoading));
        }
        Version++;
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(DisplayPath));
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(LoadError));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(StaleMessage));
        OnPropertyChanged(nameof(Version));
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Apply a targeted edit (SetDirective, DisableDirective, InsertLine, ...).</summary>
    public bool Edit(Action<ConfigDocument> edit, string? status = null)
    {
        if (Document == null)
        {
            Dialogs.Info("No configuration file is loaded.", "Open a configuration on the Configuration page or create one with the Setup wizard first.");
            return false;
        }
        try
        {
            edit(Document);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Dialogs.Error("The change could not be made.", e.Message);
            return false;
        }
        Touch();
        if (status != null) _shell.SetStatus(status + " Not saved yet: review and save on the page.");
        return true;
    }

    /// <summary>Replace the whole document (raw text editor).</summary>
    public void ReplaceDocument(ConfigDocument doc)
    {
        Document = doc;
        Touch();
    }

    private void Touch()
    {
        IsDirty = Document != null && !Document.ToBytes().AsSpan().SequenceEqual(_savedBytes);
        Version++;
        Notify();
    }

    /// <summary>Text of the document as last loaded or saved.</summary>
    public string SavedText(Encoding encoding) => encoding.GetString(StripBom(_savedBytes));

    private static ReadOnlySpan<byte> StripBom(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? b.AsSpan(3) : b.AsSpan();

    /// <summary>Discard edits: reload the selected configuration (or the file being edited).</summary>
    public async Task RevertAsync()
    {
        string? target = IsStale ? _shell.ConfigPath : Path ?? _shell.ConfigPath;
        IsDirty = false;
        if (string.IsNullOrWhiteSpace(target)) Clear();
        else await LoadAsync(target);
    }

    /// <summary>
    /// Save with a diff preview, a backup of the current file and an atomic write.  Returns true when
    /// the file was written (or nothing needed saving).
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (Document == null || Path == null) return false;
        string path = Path;
        var doc = Document;
        byte[] bytes = doc.ToBytes();
        var (diskText, diskStamp, diskSecrets) = await Task.Run(() =>
        {
            if (!File.Exists(path)) return ("", (DateTime?)null, (IReadOnlyList<string>)[]);
            var d = ConfigDocument.Load(path);
            return (d.ToText(), (DateTime?)File.GetLastWriteTimeUtc(path), ConfigChecker.FindSecrets(d));
        });
        var secrets = diskSecrets.Concat(ConfigChecker.FindSecrets(doc)).Distinct().ToList();
        var diff = TextDiff.Compute(ConfigChecker.Redact(diskText, secrets), ConfigChecker.Redact(doc.ToText(), secrets));
        bool changedOnDisk = diskStamp != _loadedWriteTimeUtc;
        if (!TextDiff.HasChanges(diff) && !changedOnDisk && bytes.AsSpan().SequenceEqual(_savedBytes))
        {
            _shell.SetStatus("No changes to save.");
            return true;
        }

        var notes = new List<string>();
        if (changedOnDisk && diskStamp != null)
            notes.Add("The file was changed by another program after it was loaded here. Saving replaces those changes with what you see on the right (the backup keeps them).");
        notes.Add(File.Exists(path)
            ? $"Before writing, the current file is copied to {_shell.Backups.BackupDirectory}."
            : "The file does not exist yet and will be created.");
        notes.Add("The file is written to a temporary file first and then swapped in, so a failure never leaves a half-written configuration.");
        if (_shell.IsDireWolfRunning) notes.Add("Dire Wolf is running: the change takes effect when you restart it.");
        notes.Add("APRS-IS passcodes are shown as ***** here; the file keeps the real value.");

        if (!ConfigDiffDialog.Show($"Save {System.IO.Path.GetFileName(path)}?", $"Changes to {path}:", diff, notes, "Save"))
            return false;

        await Task.Run(() =>
        {
            if (File.Exists(path)) _shell.Backups.Backup(path);
            AtomicFile.WriteAllBytes(path, bytes);
        });
        _savedBytes = bytes;
        _loadedWriteTimeUtc = await Task.Run(() => File.GetLastWriteTimeUtc(path));
        if (ReferenceEquals(doc, Document)) doc.MarkClean();
        Touch();
        if (_shell.IsDireWolfRunning) _shell.MarkRestartRequired();
        _shell.SetStatus($"Saved {path} (backup made).");

        if (!SamePath(path, _shell.ConfigPath))
        {
            // The edited file is no longer the selected one: load the selected file now.
            if (!string.IsNullOrWhiteSpace(_shell.ConfigPath)) await LoadAsync(_shell.ConfigPath!);
            else Clear();
        }
        else if (!string.IsNullOrWhiteSpace(_shell.DireWolfPath))
        {
            // Refresh the real-parser summary other pages use (opens no audio, PTT or network).
            try { await _shell.CheckConfigAsync(); }
            catch (Exception) { /* the Diagnostics tab reports check failures when run explicitly */ }
        }
        return true;
    }

    /// <summary>Ask before an action that replaces the document (open another file, restore).
    /// Returns false when the user cancels.</summary>
    public async Task<bool> ConfirmLeaveAsync(string action)
    {
        if (!IsDirty) return true;
        var answer = Dialogs.YesNoCancel(
            $"There are unsaved changes to \"{Path}\".\n\nSave them before you {action}?\n\nYes: review and save.  No: discard the changes.  Cancel: stay here.",
            "Unsaved changes");
        if (answer == null) return false;
        if (answer == true) return await SaveAsync();
        IsDirty = false;
        if (Path != null && File.Exists(Path)) await LoadAsync(Path);
        return true;
    }
}

/// <summary>Fire-and-forget for UI work started from property setters: failures go to the status bar.</summary>
public static class TaskExtensions
{
    public static async void Forget(this Task task, IShell shell, string what)
    {
        try { await task; }
        catch (Exception e) { shell.SetStatus($"{what}: {e.Message}"); }
    }
}

/// <summary>Texts used by every page that can enable transmitting or forwarding.</summary>
public static class OperatorNotice
{
    public const string Responsibility =
        "You, the licensed operator, are responsible for making sure that everything your station transmits " +
        "complies with your licence and the regulations that apply where you operate (frequencies, power, " +
        "identification, unattended operation, third-party traffic, content).";

    public const string NotYetSaved =
        "The change is made in the editor only. Review it and press Save: you will see exactly what changes in the file before it is written.";

    public static bool ConfirmTransmit(string what, string consequences) =>
        Dialogs.Confirm($"{what}\n\n{consequences}\n\n{Responsibility}\n\n{NotYetSaved}\n\nContinue?", "This affects what your station transmits", warning: true);
}
