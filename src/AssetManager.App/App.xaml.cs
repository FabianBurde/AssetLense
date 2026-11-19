using System.Windows;
using AssetManager.App.ViewModels;
using AssetManager.Core.Services;

namespace AssetManager.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var library = await AssetLibrary.OpenAsync();
            var viewModel = new MainViewModel(library);

            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();

            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Asset Manager could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
