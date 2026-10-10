using System.Collections.ObjectModel;
using System.Diagnostics;
using DireWolfGui.Core.Logging;
using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>
/// Dire Wolf's console output (and the application's own notes), searchable and filterable.
/// Lines are already redacted (no APRS-IS passcodes).  The view shows at most
/// <see cref="MaxShown"/> lines; the full retained history stays in the session buffer.
/// </summary>
public sealed class LogViewModel : PageViewModel
{
    public const int MaxShown = 5000;
    private readonly IShell _shell;
    private long _lastSeq;
    private string _search = "";
    private bool _showDebug, _showInfo = true, _showPackets = true, _showWarnings = true, _showErrors = true;
    private bool _paused, _autoScroll = true;
    private int _pendingWhilePaused;

    public LogViewModel(IShell shell) : base("Log", "", "Ctrl+6")
    {
        _shell = shell;
        ClearCommand = new RelayCommand(() =>
        {
            Lines.Clear();
            _lastSeq = _shell.Session.Log.LastSequence;
        });
        ExportCommand = new RelayCommand(Export);
        OpenFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(AppPaths.ConsoleLogs);
            Process.Start(new ProcessStartInfo(AppPaths.ConsoleLogs) { UseShellExecute = true });
        });
    }

    public ObservableCollection<LogEntry> Lines { get; } = [];

    public RelayCommand ClearCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public string Search { get => _search; set { if (Set(ref _search, value)) Rebuild(); } }
    public bool ShowDebug { get => _showDebug; set { if (Set(ref _showDebug, value)) Rebuild(); } }
    public bool ShowInfo { get => _showInfo; set { if (Set(ref _showInfo, value)) Rebuild(); } }
    public bool ShowPackets { get => _showPackets; set { if (Set(ref _showPackets, value)) Rebuild(); } }
    public bool ShowWarnings { get => _showWarnings; set { if (Set(ref _showWarnings, value)) Rebuild(); } }
    public bool ShowErrors { get => _showErrors; set { if (Set(ref _showErrors, value)) Rebuild(); } }
    public bool AutoScroll { get => _autoScroll; set => Set(ref _autoScroll, value); }

    public bool Paused
    {
        get => _paused;
        set
        {
            if (!Set(ref _paused, value)) return;
            if (!value) { _pendingWhilePaused = 0; Tick(); }
            OnPropertyChanged(nameof(PausedText));
        }
    }

    public string PausedText => _paused ? $"Paused — {_pendingWhilePaused} new line(s) waiting" : "";

    public string EmptyText => _shell.IsDireWolfRunning
        ? "Waiting for Dire Wolf output…"
        : "Dire Wolf is not running. Its console output appears here when you start it (F5).";

    /// <summary>Raised after lines were appended, so the view can keep the newest line visible.</summary>
    public event EventHandler? LinesAppended;

    protected override void OnShown()
    {
        OnPropertyChanged(nameof(EmptyText));
        if (_shell.Session.Log.LastSequence - _lastSeq > MaxShown) Rebuild();
        else Tick();
    }

    public override void Tick()
    {
        var log = _shell.Session.Log;
        if (_paused)
        {
            var waiting = (int)Math.Max(0, log.LastSequence - _lastSeq);
            if (waiting != _pendingWhilePaused) { _pendingWhilePaused = waiting; OnPropertyChanged(nameof(PausedText)); }
            return;
        }
        var batch = log.GetSince(_lastSeq, 2000);
        if (batch.Count == 0) return;
        _lastSeq = batch[^1].Sequence;
        var added = false;
        foreach (var e in batch)
        {
            if (!Matches(e)) continue;
            Lines.Add(e);
            added = true;
        }
        Trim();
        if (added) LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    public override void BackgroundTick() => OnPropertyChanged(nameof(EmptyText));

    private void Rebuild()
    {
        Lines.Clear();
        var log = _shell.Session.Log;
        foreach (var e in log.Snapshot().Where(Matches).TakeLast(MaxShown)) Lines.Add(e);
        _lastSeq = log.LastSequence;
        LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    private void Trim()
    {
        var excess = Lines.Count - MaxShown;
        for (var i = 0; i < excess; i++) Lines.RemoveAt(0);
    }

    internal bool Matches(LogEntry e)
    {
        var shown = e.Severity switch
        {
            LogSeverity.Debug => _showDebug,
            LogSeverity.Info => _showInfo,
            LogSeverity.Packet or LogSeverity.Transmit => _showPackets,
            LogSeverity.Warning => _showWarnings,
            LogSeverity.Error => _showErrors,
            _ => true,
        };
        return shown && (_search.Length == 0 || e.Text.Contains(_search, StringComparison.OrdinalIgnoreCase));
    }

    private void Export()
    {
        var path = Dialogs.SaveFile("Text files (*.txt)|*.txt|All files (*.*)|*.*", $"direwolf-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        if (path is null) return;
        try
        {
            File.WriteAllLines(path, _shell.Session.Log.Snapshot().Where(Matches)
                .Select(e => $"{e.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}\t{e.Severity}\t{e.Text}"));
            _shell.SetStatus($"Log exported to {path}");
        }
        catch (Exception ex)
        {
            Dialogs.Error("The log could not be exported.", ex.Message);
        }
    }
}
