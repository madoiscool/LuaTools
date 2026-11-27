using System.Windows.Controls;
using GameGuy.ViewModels;

namespace GameGuy.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView(DownloadsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
