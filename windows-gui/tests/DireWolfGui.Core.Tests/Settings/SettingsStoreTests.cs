using DireWolfGui.Core.Integrations;
using DireWolfGui.Core.Settings;
using DireWolfGui.Core.Tests.Config;

namespace DireWolfGui.Core.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void Defaults_are_conservative()
    {
        var s = new AppSettings();
        Assert.Equal(AppTheme.System, s.Theme);
        Assert.Equal(0, s.AudioStatisticsInterval);
        Assert.False(s.MessagingEnabled);
        Assert.False(s.MapTilesEnabled);
        Assert.Equal("127.0.0.1", s.AgwHost);
        Assert.False(s.FirstRunCompleted);
        Assert.EndsWith(Path.Combine("DireWolfStation", "logs"), AppSettings.DefaultCsvLogDirectory);
        Assert.Equal(AppSettings.DefaultCsvLogDirectory, s.EffectiveCsvLogDirectory);
        Assert.DoesNotContain(typeof(AppSettings).GetProperties(), p => p.Name.Contains("pass", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(Path.Combine("DireWolfStation", "settings.json"), new SettingsStore().FilePath);
    }

    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var t = new TempDir();
        var r = new SettingsStore(t.File("settings.json")).Load();
        Assert.False(r.LoadedFromFile);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Round_trip_all_fields()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File("sub/settings.json"));
        var s = new AppSettings
        {
            DireWolfExePath = @"C:\dw\direwolf.exe", ConfigPath = @"C:\dw\direwolf.conf", WorkingDirectory = @"C:\dw",
            Theme = AppTheme.Dark, AudioStatisticsInterval = 30, CsvLogEnabled = true, CsvLogDirectory = @"D:\logs",
            AgwPortOverride = 8010, MessagingEnabled = true, MapTilesEnabled = true, HomeLatitude = 42.5, HomeLongitude = -71.2,
            Window = new WindowPlacement { Left = 10, Top = 20, Width = 900, Height = 700, Maximized = true },
            PanelSizes = { ["log"] = 250.5 }, FirstRunCompleted = true, LastWorkspacePage = "Monitor",
            ExternalApps = [new ExternalAppProfile { Name = "YAAC", TemplateId = "yaac", Arguments = ["-jar", "YAAC.jar"], Protocol = TncProtocol.KissTcp, Port = 8001 }],
        };
        store.Save(s);
        string json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"theme\": \"Dark\"", json);
        var r = store.Load();
        Assert.True(r.LoadedFromFile);
        var l = r.Settings;
        Assert.Equal((AppTheme.Dark, 30, 8010, true, 42.5), (l.Theme, l.AudioStatisticsInterval, l.AgwPortOverride!.Value, l.Window!.Maximized, l.HomeLatitude!.Value));
        Assert.Equal(250.5, l.PanelSizes["log"]);
        Assert.Equal(["-jar", "YAAC.jar"], l.ExternalApps.Single().Arguments);
        Assert.Equal(TncProtocol.KissTcp, l.ExternalApps[0].Protocol);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{\"theme\": \"Purple\"}")]
    [InlineData("[1,2,3]")]
    public void Corrupt_file_is_kept_and_defaults_used(string content)
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File("settings.json"));
        File.WriteAllText(store.FilePath, content);
        var r = store.Load();
        Assert.False(r.LoadedFromFile);
        Assert.NotNull(r.Error);
        Assert.NotNull(r.CorruptCopyPath);
        Assert.Equal(content, File.ReadAllText(r.CorruptCopyPath!));
        Assert.Equal(AppTheme.System, r.Settings.Theme);
    }

    [Fact]
    public void Unknown_properties_like_passcodes_are_dropped_and_values_normalized()
    {
        using var t = new TempDir();
        var store = new SettingsStore(t.File("settings.json"));
        File.WriteAllText(store.FilePath, "{ \"passcode\": \"12345\", \"igPasscode\": 12345, \"logRetentionLines\": 5, \"homeLatitude\": 123, \"agwHost\": \"\", // comment\n }");
        var r = store.Load();
        Assert.True(r.LoadedFromFile);
        Assert.Equal(100, r.Settings.LogRetentionLines);
        Assert.Null(r.Settings.HomeLatitude);
        Assert.Equal("127.0.0.1", r.Settings.AgwHost);
        store.Save(r.Settings);
        Assert.DoesNotContain("12345", File.ReadAllText(store.FilePath));
        Assert.DoesNotContain("passcode", File.ReadAllText(store.FilePath), StringComparison.OrdinalIgnoreCase);
    }
}
