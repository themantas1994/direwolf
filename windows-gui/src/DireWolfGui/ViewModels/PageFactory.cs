namespace DireWolfGui.ViewModels;

/// <summary>The workspace pages, in navigation order (Ctrl+1 … Ctrl+9 follow this order).</summary>
internal static class PageFactory
{
    public static IEnumerable<PageViewModel> CreatePages(MainViewModel main)
    {
        yield return new DashboardViewModel(main);
        yield return new LogViewModel(main);
    }
}
