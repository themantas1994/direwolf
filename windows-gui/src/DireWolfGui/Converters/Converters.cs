using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DireWolfGui.Converters;

/// <summary>true → Visible; false/null → Collapsed.  Parameter "invert" reverses it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ (parameter as string == "invert");
}

/// <summary>Non-null and non-empty → Visible.  Parameter "invert" reverses it.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => s.Length > 0,
            System.Collections.ICollection c => c.Count > 0,
            int i => i != 0,
            _ => true,
        };
        if (parameter as string == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Looks up a theme brush by name: the bound value (an enum or string) is mapped to a
/// resource key "{value}Brush", e.g. Error → ErrorBrush.  Unknown values use NeutralBrush.
/// </summary>
public sealed class ThemeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "Neutral") + "Brush";
        return Application.Current?.TryFindResource(key) as Brush
            ?? Application.Current?.TryFindResource("NeutralBrush") as Brush
            ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats a TimeSpan as d.hh:mm:ss / hh:mm:ss.</summary>
public sealed class DurationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TimeSpan t
            ? (t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t:hh\\:mm\\:ss}" : t.ToString(@"hh\:mm\:ss"))
            : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Local time of a DateTimeOffset, format from the parameter (default HH:mm:ss).</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset d => d.ToLocalTime().ToString(parameter as string ?? "HH:mm:ss", culture),
        DateTime d => d.ToLocalTime().ToString(parameter as string ?? "HH:mm:ss", culture),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>"how long ago" text for a time, e.g. "12 s", "5 min", "3 h", "2 d".</summary>
public sealed class AgeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTimeOffset d) return "";
        return Format(DateTimeOffset.Now - d);
    }

    public static string Format(TimeSpan a) =>
        a.TotalSeconds < 60 ? $"{Math.Max(0, (int)a.TotalSeconds)} s"
        : a.TotalMinutes < 60 ? $"{(int)a.TotalMinutes} min"
        : a.TotalHours < 48 ? $"{(int)a.TotalHours} h"
        : $"{(int)a.TotalDays} d";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
