using System.Windows;
using AssetManager.App.Services;
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
            // Theme first, so the window (and any error dialog) opens in the right colors.
            var theme = new ThemeService(AppSettings.Load());
            theme.Initialize();

            var library = await AssetLibrary.OpenAsync();
            var viewModel = new MainViewModel(library, theme);

            var window = new MainWindow(viewModel);
            theme.Attach(window);
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
