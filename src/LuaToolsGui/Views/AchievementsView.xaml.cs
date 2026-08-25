using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class AchievementsView : UserControl
{
    private readonly AchievementsViewModel _viewModel;

    public AchievementsView(AchievementsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        Loaded += async (_, _) =>
        {
            try
            {
                if (_viewModel.AllGames.Count == 0 && !_viewModel.IsGameSelected)
                {
                    await _viewModel.LoadGamesCommand.ExecuteAsync(false);
                }
            }
            catch { /* non-fatal */ }
        };
    }
}
