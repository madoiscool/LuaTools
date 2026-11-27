using System.Windows.Controls;

namespace GameGuy.Views;

/// <summary>
/// Page-level overwrite-confirm overlay (dim scrim + centered diff card). Host it at a page root with
/// DataContext bound to the page's <see cref="ViewModels.DropInstallViewModel"/>; it shows itself via
/// the VM's IsConfirming. An in-window overlay (not a Popup) so it has no system drop-shadow artifact.
/// </summary>
public partial class ConfirmOverlay : UserControl
{
    public ConfirmOverlay() => InitializeComponent();
}
