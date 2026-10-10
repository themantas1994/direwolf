using System.Text.Json;
using System.Text.Json.Serialization;
using DireWolfGui.Core.Config;

namespace DireWolfGui.Core.Settings;

public sealed record SettingsLoadResult(AppSettings Settings, bool LoadedFromFile, string? CorruptCopyPath, string? Error);

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON (default %APPDATA%\DireWolfStation\settings.json).
/// Writes are atomic.  A file that cannot be read is kept as settings.json.corrupt-&lt;time&gt; and defaults are used.
/// </summary>
public sealed class SettingsStore(string? path = null)
{
    public string FilePath { get; } = path ?? Path.Combine(AppSettings.AppDataDirectory, "settings.json");

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        // Unknown properties (e.g. a hand-added "passcode") are dropped on the next save.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath)) return new SettingsLoadResult(new AppSettings(), false, null, null);
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? throw new JsonException("The file contains null.");
            s.Normalize();
            return new SettingsLoadResult(s, true, null, null);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            string? copy = null;
            try
            {
                copy = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                File.Copy(FilePath, copy, overwrite: true);
            }
            catch (Exception ce) when (ce is IOException or UnauthorizedAccessException) { copy = null; }
            return new SettingsLoadResult(new AppSettings(), false, copy, $"Settings could not be read ({e.Message}); defaults are used.");
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
