using System.Windows;
using TfmStudio.App.ViewModels;
using TfmStudio.App.Views;

namespace TfmStudio.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var presenter = new PlotPresenter(TfmPlot, SectorPlot, CScanPlot, AScanPlot);
        viewModel.FrameReady += presenter.Render;
        Closing += (_, _) =>
        {
            if (viewModel.StopCommand.CanExecute(null)) viewModel.StopCommand.Execute(null);
        };
    }
}
