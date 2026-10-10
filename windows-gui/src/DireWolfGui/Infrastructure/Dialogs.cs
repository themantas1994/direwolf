using System.Windows;
using Microsoft.Win32;

namespace DireWolfGui.Infrastructure;

/// <summary>Consistent message boxes and file pickers.</summary>
public static class Dialogs
{
    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } w ? w : null;

    public static void Error(string message, string? detail = null) =>
        Show(message, detail, MessageBoxImage.Error);

    public static void Info(string message, string? detail = null) =>
        Show(message, detail, MessageBoxImage.Information);

    public static void Warning(string message, string? detail = null) =>
        Show(message, detail, MessageBoxImage.Warning);

    private static void Show(string message, string? detail, MessageBoxImage image)
    {
        var text = string.IsNullOrWhiteSpace(detail) ? message : message + "\n\n" + detail;
        if (Owner is { } o) MessageBox.Show(o, text, "Dire Wolf Station", MessageBoxButton.OK, image);
        else MessageBox.Show(text, "Dire Wolf Station", MessageBoxButton.OK, image);
    }

    /// <summary>Yes/No confirmation; defaults to No so Enter never confirms something risky.</summary>
    public static bool Confirm(string message, string title = "Please confirm", bool warning = false)
    {
        var image = warning ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var r = Owner is { } o
            ? MessageBox.Show(o, message, title, MessageBoxButton.YesNo, image, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, image, MessageBoxResult.No);
        return r == MessageBoxResult.Yes;
    }

    /// <summary>Yes/No/Cancel; returns null for Cancel.</summary>
    public static bool? YesNoCancel(string message, string title)
    {
        var r = Owner is { } o
            ? MessageBox.Show(o, message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            : MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        return r switch { MessageBoxResult.Yes => true, MessageBoxResult.No => false, _ => null };
    }

    public static string? OpenFile(string filter, string? initialPath = null, string? title = null)
    {
        var d = new OpenFileDialog { Filter = filter, Title = title ?? "Open", CheckFileExists = true };
        SetInitial(d, initialPath);
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? SaveFile(string filter, string defaultName, string? initialPath = null)
    {
        var d = new SaveFileDialog { Filter = filter, FileName = defaultName, OverwritePrompt = true };
        SetInitial(d, initialPath);
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? PickFolder(string? initialPath = null, string? title = null)
    {
        var d = new OpenFolderDialog { Title = title ?? "Choose folder" };
        if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath)) d.InitialDirectory = initialPath;
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    private static void SetInitial(FileDialog d, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) d.InitialDirectory = dir;
    }
}
