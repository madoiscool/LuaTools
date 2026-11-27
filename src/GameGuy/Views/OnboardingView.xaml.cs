using System.Windows.Controls;

namespace GameGuy.Views;

/// <summary>
/// First-run welcome overlay (dim scrim + centered card). Hosted at the MainWindow root with its
/// DataContext bound to <see cref="ViewModels.OnboardingViewModel"/>; the host controls visibility via
/// the VM's IsOpen. An in-window overlay (not a Popup) so it has no system drop-shadow artifact.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView() => InitializeComponent();
}
