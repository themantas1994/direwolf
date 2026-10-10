using DireWolfGui.Core.Config;
using DireWolfGui.Core.Settings;

namespace DireWolfGui.ViewModels;

/// <summary>Result of a TCP / AGW / KISS connection test, in plain words.</summary>
public sealed record ConnectionCheck(bool Success, string Summary, string? Detail);

/// <summary>
/// What pages may ask of the main window: settings, Dire Wolf process control, the
/// station's console output, and navigation.  Implemented by MainViewModel.
/// All members are called on the UI thread; events are raised on the UI thread.
/// </summary>
public interface IShell
{
    AppSettings Settings { get; }
    void SaveSettings();

    /// <summary>Configuration file currently selected for Dire Wolf (Settings.ConfigPath).</summary>
    string? ConfigPath { get; }
    /// <summary>direwolf.exe currently selected (Settings.DireWolfPath).</summary>
    string? DireWolfPath { get; }
    /// <summary>Select another configuration file (persisted); raises StationStateChanged.</summary>
    void SelectConfig(string path);
    void SelectDireWolf(string path);

    ConfigBackupManager Backups { get; }
    ProfileManager Profiles { get; }

    bool IsDireWolfRunning { get; }
    /// <summary>Latest real-parser check of the selected configuration (null until one ran).</summary>
    CheckConfigResult? LastCheck { get; }
    /// <summary>Run the real parser on the selected configuration and remember the result.</summary>
    Task<CheckConfigResult?> CheckConfigAsync();

    /// <summary>Starts Dire Wolf with the selected configuration, or with <paramref name="configOverride"/>
    /// (e.g. the wizard's receive-only test file).  Never starts transmit tests.</summary>
    Task StartDireWolfAsync(string? configOverride = null);
    Task StopDireWolfAsync();
    Task RestartDireWolfAsync();

    /// <summary>Call after saving the configuration: shows "restart needed" while Dire Wolf runs.</summary>
    void MarkRestartRequired();

    /// <summary>Each line Dire Wolf prints (already redacted), as it arrives.</summary>
    event EventHandler<string>? ConsoleLine;
    /// <summary>Process started/stopped, configuration or executable changed.</summary>
    event EventHandler? StationStateChanged;

    Task<ConnectionCheck> TestConnectionAsync(string protocol, string host, int port);

    void Navigate<T>() where T : PageViewModel;
    void SetStatus(string message);
}
