using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using GameGuy.Services;
using GameGuy.ViewModels;

namespace GameGuy.Views;

public partial class FixesView : UserControl
{
    private readonly FixesViewModel _viewModel;

    public FixesView(FixesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        // On a page change, jump the grid back to the top (the new page starts fresh).
        _viewModel.ScrollToTop = ScrollGridToTop;
        Loaded += async (_, _) => await _viewModel.LoadAsync();

        // Markdig.Wpf wires each link's Click to this command (parameter = the URL). Open it in the
        // system browser. (It also sets NavigateUri; RequestNavigate is suppressed below so WPF doesn't
        // try to navigate the in-app content to the URL.)
        CommandBindings.Add(new CommandBinding(Markdig.Wpf.Commands.Hyperlink, OpenHyperlink));
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler((_, e) => e.Handled = true));
    }

    private static void OpenHyperlink(object sender, ExecutedRoutedEventArgs e)
    {
        Open(e.Parameter as string);
        e.Handled = true;
    }

    private static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { SteamService.OpenUrl(url); } catch { /* bad/blocked URL. Ignore */ }
    }

    // MarkdownViewer hosts its own (non-scrolling) FlowDocumentScrollViewer that swallows the mouse
    // wheel, so the flyout's outer ScrollViewer never sees it. Re-raise the wheel event up the tree
    // so the parent scrolls when the pointer is over the markdown text.
    private void Markdown_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement el) return;
        e.Handled = true;
        el.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        });
    }

    /// <summary>Scroll the game grid back to offset 0. Dispatched so it runs after the new page's items
    /// have been laid out (the ScrollViewer exists once the control is templated).</summary>
    private void ScrollGridToTop() => Dispatcher.BeginInvoke(() =>
    {
        if (FindDescendant<ScrollViewer>(GameScroller) is { } sv) sv.ScrollToTop();
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

    private void Scrim_Click(object sender, MouseButtonEventArgs e) =>
        _viewModel.CloseDetailCommand.Execute(null);

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e) =>
        _viewModel.CloseDetailCommand.Execute(null);
}
