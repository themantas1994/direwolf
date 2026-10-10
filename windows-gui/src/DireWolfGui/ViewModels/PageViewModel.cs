using DireWolfGui.Infrastructure;

namespace DireWolfGui.ViewModels;

/// <summary>
/// A workspace page.  The shell calls <see cref="Tick"/> about four times a second while
/// the page is visible, so pages pull new data from the station session in batches
/// instead of reacting to every received frame.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(string title, string glyph, string? shortcut = null)
    {
        Title = title;
        Glyph = glyph;
        Shortcut = shortcut;
    }

    public string Title { get; }
    public string Glyph { get; }
    public string? Shortcut { get; }
    public string ToolTip => Shortcut is null ? Title : $"{Title} ({Shortcut})";

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!Set(ref _isActive, value)) return;
            if (value) OnShown(); else OnHidden();
        }
    }

    protected virtual void OnShown() { }
    protected virtual void OnHidden() { }

    /// <summary>Periodic UI refresh while visible.</summary>
    public virtual void Tick() { }

    /// <summary>Background work that must happen even when not visible (cheap).</summary>
    public virtual void BackgroundTick() { }
}
