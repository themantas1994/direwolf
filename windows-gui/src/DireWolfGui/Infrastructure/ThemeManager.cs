using System.Windows;
using Microsoft.Win32;

namespace DireWolfGui.Infrastructure;

/// <summary>
/// Light / dark / follow-Windows themes.  Controls use the WPF Fluent theme (ThemeMode);
/// the application's own semantic brushes (status colours, packet colours) come from
/// Themes/Light.xaml or Themes/Dark.xaml, swapped here.
/// </summary>
public static class ThemeManager
{
    private const string LightUri = "Themes/Light.xaml";
    private const string DarkUri = "Themes/Dark.xaml";
    private static string _mode = "System";

    public static event EventHandler? ThemeChanged;

    public static bool IsDark { get; private set; }

    public static string Mode => _mode;

    public static void Initialize(string mode)
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_mode == "System" && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply("System"));
        };
        Apply(mode);
    }

    public static void Apply(string mode)
    {
        _mode = mode is "Light" or "Dark" ? mode : "System";
        var dark = _mode == "Dark" || (_mode == "System" && WindowsUsesDarkApps());
        var app = Application.Current;
        if (app is null) return;

#pragma warning disable WPF0001 // ThemeMode is marked experimental but supported in .NET 9+.
        app.ThemeMode = _mode switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

        var dicts = app.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d => d.Source is { } s &&
            (s.OriginalString.EndsWith(LightUri, StringComparison.OrdinalIgnoreCase) ||
             s.OriginalString.EndsWith(DarkUri, StringComparison.OrdinalIgnoreCase)));
        var next = new ResourceDictionary { Source = new Uri(dark ? DarkUri : LightUri, UriKind.Relative) };
        if (existing is null) dicts.Add(next);
        else dicts[dicts.IndexOf(existing)] = next;

        IsDark = dark;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
