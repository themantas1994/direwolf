using DireWolfGui.Core.Integrations;

namespace DireWolfGui.Core.Settings;

public enum AppTheme { System, Light, Dark }

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 800;
    public bool Maximized { get; set; }
}

/// <summary>
/// GUI settings, stored as JSON by <see cref="SettingsStore"/>.  There is deliberately no field
/// for an APRS-IS passcode: credentials live only in the Dire Wolf configuration file.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // Dire Wolf
    public string? DireWolfExePath { get; set; }
    public string? ConfigPath { get; set; }
    public string? WorkingDirectory { get; set; }
    /// <summary>Seconds between audio statistics reports (direwolf -a); 0 = off.</summary>
    public int AudioStatisticsInterval { get; set; }

    // Appearance
    public AppTheme Theme { get; set; } = AppTheme.System;
    public WindowPlacement? Window { get; set; }
    public Dictionary<string, double> PanelSizes { get; set; } = new();
    public string? LastWorkspacePage { get; set; }
    public bool FirstRunCompleted { get; set; }

    // Logs
    public bool CsvLogEnabled { get; set; } = true;
    /// <summary>Directory for Dire Wolf's daily CSV logs (direwolf -l); null = <see cref="DefaultCsvLogDirectory"/>.</summary>
    public string? CsvLogDirectory { get; set; }
    public int LogRetentionLines { get; set; } = 20000;
    public int PacketRetentionCount { get; set; } = 10000;

    // Monitor connection (AGW)
    public string AgwHost { get; set; } = "127.0.0.1";
    /// <summary>AGW port override; null = take it from the configuration (default 8000).</summary>
    public int? AgwPortOverride { get; set; }
    public bool AutoConnectMonitor { get; set; } = true;

    // Messaging (transmits; off until the operator enables it)
    public bool MessagingEnabled { get; set; }
    public bool AutoAck { get; set; } = true;
    public int MessageRetryCount { get; set; } = 5;
    public int MessageRetrySeconds { get; set; } = 30;

    // Map (network access; off until enabled)
    public bool MapTilesEnabled { get; set; }
    public string MapTileUrlTemplate { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    public string MapAttribution { get; set; } = "© OpenStreetMap contributors";
    public string? MapTileCacheDirectory { get; set; }
    public double? HomeLatitude { get; set; }
    public double? HomeLongitude { get; set; }

    // Integrations
    public List<ExternalAppProfile> ExternalApps { get; set; } = [];

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "DireWolfStation");

    public static string LocalDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "DireWolfStation");

    /// <summary>%LOCALAPPDATA%\DireWolfStation\logs</summary>
    public static string DefaultCsvLogDirectory => Path.Combine(LocalDataDirectory, "logs");

    public string EffectiveCsvLogDirectory => string.IsNullOrWhiteSpace(CsvLogDirectory) ? DefaultCsvLogDirectory : CsvLogDirectory!;
    public string EffectiveTileCacheDirectory => string.IsNullOrWhiteSpace(MapTileCacheDirectory) ? Path.Combine(LocalDataDirectory, "tiles") : MapTileCacheDirectory!;

    /// <summary>Clamp values read from a file into sensible ranges.</summary>
    public void Normalize()
    {
        AudioStatisticsInterval = Math.Clamp(AudioStatisticsInterval, 0, 3600);
        LogRetentionLines = Math.Clamp(LogRetentionLines, 100, 1_000_000);
        PacketRetentionCount = Math.Clamp(PacketRetentionCount, 100, 1_000_000);
        MessageRetryCount = Math.Clamp(MessageRetryCount, 0, 20);
        MessageRetrySeconds = Math.Clamp(MessageRetrySeconds, 5, 3600);
        if (AgwPortOverride is int p && (p < 1 || p > 65535)) AgwPortOverride = null;
        if (string.IsNullOrWhiteSpace(AgwHost)) AgwHost = "127.0.0.1";
        if (HomeLatitude is double la && (la < -90 || la > 90)) HomeLatitude = null;
        if (HomeLongitude is double lo && (lo < -180 || lo > 180)) HomeLongitude = null;
        PanelSizes ??= new();
        ExternalApps ??= [];
        ExternalApps.RemoveAll(a => a == null);
        if (string.IsNullOrWhiteSpace(MapTileUrlTemplate)) MapTileUrlTemplate = new AppSettings().MapTileUrlTemplate;
        MapAttribution ??= "";
    }
}
