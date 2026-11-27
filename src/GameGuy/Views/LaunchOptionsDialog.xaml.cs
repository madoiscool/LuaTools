using GameGuy.ViewModels;

namespace GameGuy.Views;

/// <summary>
/// Modal launch-option editor, opened from the Manage flyout. Mirrors SteamEdit's LaunchEditor: entry
/// list on the left, fields for the selected entry on the right.
/// </summary>
public partial class LaunchOptionsDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly LaunchOptionsViewModel _viewModel;

    public LaunchOptionsDialog(LaunchOptionsViewModel viewModel, long appId, string gameName)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        // The VM owns the outcome; the window just closes when told to. Guarded because Save can run
        // before the window is shown if loading fails instantly.
        _viewModel.CloseWith = ok =>
        {
            if (!IsLoaded) return;
            DialogResult = ok;
            Close();
        };

        Loaded += async (_, _) => await _viewModel.LoadAsync(appId, gameName);
    }
}
