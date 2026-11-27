using System.Windows;
using System.Windows.Controls;
using GameGuy.ViewModels;

namespace GameGuy.Views;

public partial class ManageView : UserControl
{
    private readonly ManageViewModel _viewModel;

    public ManageView(ManageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        // On a page change, jump the grid back to the top (the new page starts fresh).
        _viewModel.ScrollToTop = ScrollGridToTop;
        // Re-scan stplug-in each time the page is shown (lua files change as games are added).
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    /// <summary>Scroll the tile grid back to offset 0. Dispatched so it runs after the new page's items
    /// have been laid out (the ScrollViewer exists once the control is templated).</summary>
    private void ScrollGridToTop() => Dispatcher.BeginInvoke(() =>
    {
        if (FindDescendant<ScrollViewer>(TileScroller) is { } sv) sv.ScrollToTop();
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

    // Both fire as a virtualized tile is realized/scrolled into view. Resolve its cover.
    // EnsureResolvedAsync is idempotent (skips if a cover is already loaded), so double-firing is safe.
    private void Tile_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is LuaTileViewModel tile) _viewModel.ResolveTile(tile);
    }

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LuaTileViewModel tile }) _viewModel.ResolveTile(tile);
    }

    // Click a tile → toggle selection while in selection mode, otherwise open the detail flyout.
    private void Tile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Ignore clicks on a button/checkbox so they don't also trigger the tile action.
        if (e.OriginalSource is DependencyObject src && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) is not null)
            return;
        if (sender is not FrameworkElement { DataContext: LuaTileViewModel tile }) return;

        if (_viewModel.IsSelecting) tile.IsSelected = !tile.IsSelected;
        else _viewModel.OpenDetailCommand.Execute(tile);
    }

    private void ToggleSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LuaTileViewModel tile })
            tile.IsSelected = !tile.IsSelected;
    }

    private void Scrim_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _viewModel.CloseDetailCommand.Execute(null);

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
