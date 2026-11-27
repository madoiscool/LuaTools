using System.Windows.Controls;
using System.Windows.Input;
using GameGuy.ViewModels;

namespace GameGuy.Views;

public partial class DownloadView : UserControl
{
    public DownloadView(DownloadViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // Warm the featured strips in the background (best-effort; no-op if already loaded).
        _ = viewModel.LoadFeaturedAsync();
        // Pull the latest FastFetch value each time the view appears (it's also toggleable in Settings).
        Loaded += (_, _) => viewModel.SyncFastFetch();
    }

    /// <summary>The featured strips scroll horizontally, but they're nested inside the page's vertical
    /// ScrollViewer, so a normal mouse wheel would bubble up and scroll the page instead. Translate the
    /// vertical wheel delta into horizontal scroll while the pointer is over a strip, and mark it handled
    /// so the outer ScrollViewer doesn't also move.</summary>
    private void FeaturedStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
