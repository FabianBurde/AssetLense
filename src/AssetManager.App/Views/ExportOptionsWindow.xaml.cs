using System.Windows;
using AssetManager.App.Services;
using AssetManager.Core.Bundling;

namespace AssetManager.App.Views;

public partial class ExportOptionsWindow : Window
{
    public ExportOptionsWindow(string name, int count, LicenseReport report)
    {
        InitializeComponent();
        ThemeService.Current?.Attach(this);

        SummaryText.Text = $"Export {count:N0} asset(s) from “{name}” as a zip";

        if (report.HasIssues)
        {
            WarningText.Text =
                string.Join("\n", report.Issues.Select(i =>
                    $"{i.Count:N0} with {i.Category}: {string.Join(", ", i.Examples)}{(i.Count > i.Examples.Count ? ", …" : "")}")) +
                "\n\nMake sure you may share these before you send the zip to anyone.";
            WarningBox.Visibility = Visibility.Visible;
        }
    }

    public BundleExportOptions Options => new(
        KeepFoldersRadio.IsChecked == true ? ZipLayout.KeepFolders
            : FlatRadio.IsChecked == true ? ZipLayout.Flat
            : ZipLayout.ByType,
        SnakeCheck.IsChecked == true,
        LicensesCheck.IsChecked == true,
        CreditsCheck.IsChecked == true);

    private void Export_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
