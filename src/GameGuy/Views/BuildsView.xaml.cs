using System.Windows;
using System.Windows.Controls;
using GameGuy.ViewModels;

namespace GameGuy.Views;

public partial class BuildsView : UserControl
{
    private readonly BuildsViewModel _viewModel;

    public BuildsView(BuildsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        // On a page change, jump the list back to the top (the new page starts fresh).
        _viewModel.ScrollToTop = ScrollListToTop;
        // Re-scan stplug-in each time the page is shown (lua files change as games are added/switched).
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    /// <summary>Scroll the game list back to offset 0. Dispatched so it runs after the new page's items
    /// have been laid out (the ScrollViewer exists once the control is templated).</summary>
    private void ScrollListToTop() => Dispatcher.BeginInvoke(() =>
    {
        if (FindDescendant<ScrollViewer>(GameList) is { } sv) sv.ScrollToTop();
    }, System.Windows.Threading.DispatcherPriority.Background);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    // Both fire as a virtualized game row is realized/scrolled into view. Resolve its cover.
    // EnsureResolvedAsync is idempotent (skips if a cover is already loaded), so double-firing is safe.
    private void GameRow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is LuaTileViewModel game) _viewModel.ResolveGame(game);
    }

    private void GameRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LuaTileViewModel game }) _viewModel.ResolveGame(game);
    }
}
