using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AssetManager.App.ViewModels;

namespace AssetManager.App;

public partial class MainWindow : Window
{
    /// <summary>Marks a drag that started inside this app (so quick targets can use the current selection).</summary>
    private const string InternalDragFormat = "AssetManager.Assets";

    private Point _dragStart;
    private bool _dragCandidate;
    private ListBoxItem? _pendingCollapse;

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

    private void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.OpenSelectedCommand.CanExecute(null))
            ViewModel.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }

    // Right-clicking an unselected tile selects just that tile first (like Explorer does).
    private void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item && !item.IsSelected &&
            ItemsControl.ItemsControlFromItemContainer(item) is ListBox list)
        {
            list.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    // ---------------------------------------------------------------- dragging files out of the grid

    private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = false;
        _pendingCollapse = null;

        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return; // the favorite star
        if (sender is not ListBoxItem item) return;

        _dragStart = e.GetPosition(null);
        _dragCandidate = true;

        // WPF would collapse a multi-selection to the clicked tile on mouse-down, which makes dragging
        // several files impossible. Keep the selection now; collapse on mouse-up if no drag happened.
        if (item.IsSelected && Keyboard.Modifiers == ModifierKeys.None &&
            ItemsControl.ItemsControlFromItemContainer(item) is ListBox list && list.SelectedItems.Count > 1)
        {
            _pendingCollapse = item;
            item.Focus();
            e.Handled = true;
        }
    }

    private void Grid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingCollapse is { } item &&
            ItemsControl.ItemsControlFromItemContainer(item) is ListBox list)
        {
            list.SelectedItems.Clear();
            item.IsSelected = true;
        }
        _pendingCollapse = null;
        _dragCandidate = false;
    }

    private void Grid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed) return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragCandidate = false;
        _pendingCollapse = null; // a drag keeps the whole selection

        var paths = ViewModel.PrepareDrag();
        if (paths is null) return;

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths.ToArray()); // what Explorer and the Godot editor understand
        data.SetData(InternalDragFormat, true);

        // Copy only: Explorer never turns this into a move, so the library files can't be moved away.
        var effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
        ViewModel.OnDragFinished(paths.Count, effect);
    }

    // ---------------------------------------------------------------- dropping onto quick targets

    private void Target_DragEnter(object sender, DragEventArgs e)
    {
        Target_DragOver(sender, e);
        if (e.Effects != DragDropEffects.None && sender is Panel panel)
            panel.Background = (Brush)FindResource("Brush.DropHighlight");
    }

    private void Target_DragOver(object sender, DragEventArgs e)
    {
        var accepted = e.Data.GetDataPresent(InternalDragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Target_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Panel panel) panel.Background = Brushes.Transparent;
    }

    private void Target_Drop(object sender, DragEventArgs e)
    {
        var panel = sender as Panel;
        if (panel is not null) panel.Background = Brushes.Transparent;
        e.Handled = true;

        if (panel?.DataContext is not QuickTargetViewModel target) return;

        var fromGrid = e.Data.GetDataPresent(InternalDragFormat);
        var externalFiles = e.Data.GetData(DataFormats.FileDrop) as string[];

        // Run after the drop has finished: dialogs (conflicts) must not open inside the drag operation.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (fromGrid)
                    await ViewModel.CopySelectionToAsync(target);
                else if (externalFiles is not null)
                    await ViewModel.CopyFilesToTargetAsync(target, externalFiles);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }), DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- dropping tiles onto a bundle in the sidebar

    private static bool IsBundleDrop(object sender, DragEventArgs e) =>
        e.Data.GetDataPresent(InternalDragFormat) &&
        sender is FrameworkElement { DataContext: FolderNodeViewModel { Kind: NodeKind.Bundle } };

    // Anything else (like folders dragged in from Explorer) is left alone and handled by the tree itself.
    private void Node_DragEnter(object sender, DragEventArgs e)
    {
        if (IsBundleDrop(sender, e) && sender is Panel panel)
            panel.Background = (Brush)FindResource("Brush.DropHighlight");
    }

    private void Node_DragOver(object sender, DragEventArgs e)
    {
        if (!IsBundleDrop(sender, e)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Node_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Panel panel) panel.Background = Brushes.Transparent;
    }

    private void Node_Drop(object sender, DragEventArgs e)
    {
        if (sender is Panel panel) panel.Background = Brushes.Transparent;
        if (!IsBundleDrop(sender, e)) return;

        var node = (FolderNodeViewModel)((FrameworkElement)sender).DataContext;
        var bundleId = node.BundleId!.Value; // IsBundleDrop guarantees this is a bundle node
        e.Handled = true;
        var name = node.Title;

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await ViewModel.AddSelectionToBundleAsync(bundleId, name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Could not add to the bundle", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }), DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- dropping folders onto the sidebar

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

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match) return match;
            // OriginalSource can be a Run or other non-visual element; walk the logical tree for those.
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
