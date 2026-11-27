using System.Windows.Controls;
using GameGuy.ViewModels;

namespace GameGuy.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _vm;

    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;
        Loaded += (_, _) => _vm.OnViewLoaded();
    }
}
