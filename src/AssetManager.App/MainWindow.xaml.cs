using System.IO;
using System.Windows;
using System.Windows.Controls;
using AssetManager.App.ViewModels;

namespace AssetManager.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // ListBox.SelectedItems can't be bound, so the view forwards the selection.
    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var list = (ListBox)sender;
        ViewModel.UpdateSelection(list.SelectedItems.Cast<AssetItemViewModel>());
    }

    private void Item_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ViewModel.OpenSelectedCommand.CanExecute(null))
            ViewModel.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }

    // Right-clicking an unselected tile selects just that tile first (like Explorer does).
    private void Item_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && !item.IsSelected &&
            ItemsControl.ItemsControlFromItemContainer(item) is ListBox list)
        {
            list.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void Folders_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Folders_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _ = ViewModel.AddFoldersAsync(paths.Where(Directory.Exists));
        e.Handled = true;
    }
}
