using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using AssetManager.Core.Models;
using AssetManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AssetManager.App.ViewModels;

public sealed record TypeOption(string Label, AssetType? Type);

public sealed partial class MainViewModel : ObservableObject, ISidebarActions
{
    private readonly AssetLibrary _library;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _syncCts;
    private SyncPhase _lastPhase = SyncPhase.Done;
    private FolderNodeViewModel? _selectedFolder;
    private bool _suppressFolderSelection;
    private IReadOnlyList<AssetItemViewModel> _selectedAssets = Array.Empty<AssetItemViewModel>();

    public MainViewModel(AssetLibrary library)
    {
        _library = library;
        _selectedType = TypeOptions[0];
    }

    public IReadOnlyList<TypeOption> TypeOptions { get; } = new TypeOption[]
    {
        new("All types", null),
        new("Textures", AssetType.Texture),
        new("Meshes", AssetType.Mesh),
        new("Materials", AssetType.Material),
        new("Audio", AssetType.Audio),
        new("Animations", AssetType.Animation),
        new("Shaders", AssetType.Shader),
        new("Other", AssetType.Other),
    };

    [ObservableProperty] private IReadOnlyList<AssetItemViewModel> _items = Array.Empty<AssetItemViewModel>();
    [ObservableProperty] private IReadOnlyList<FolderNodeViewModel> _folders = Array.Empty<FolderNodeViewModel>();
    [ObservableProperty] private IReadOnlyList<QuickTargetViewModel> _quickTargets = Array.Empty<QuickTargetViewModel>();
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private TypeOption _selectedType;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private int _resultCount;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isProgressIndeterminate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    private bool _isBusy;

    public bool HasSelection => _selectedAssets.Count > 0;

    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();
    partial void OnSelectedTypeChanged(TypeOption value) => _ = RefreshAsync();

    public async Task InitializeAsync()
    {
        await ReloadSidebarAsync();
        await RefreshAsync();
    }

    // ---------------------------------------------------------------- selection

    /// <summary>Called by the view whenever the grid selection changes (SelectedItems isn't bindable).</summary>
    public void UpdateSelection(IEnumerable<AssetItemViewModel> selected)
    {
        _selectedAssets = selected.ToList();
        SelectionText = _selectedAssets.Count switch
        {
            0 => "",
            1 => _selectedAssets[0].RelativePath,
            var n => $"{n:N0} selected",
        };

        OnPropertyChanged(nameof(HasSelection));
        CopyFilesCommand.NotifyCanExecuteChanged();
        CopyPathsCommand.NotifyCanExecuteChanged();
        ShowInExplorerCommand.NotifyCanExecuteChanged();
        OpenSelectedCommand.NotifyCanExecuteChanged();
        foreach (var target in QuickTargets)
            target.RefreshCanCopy();
    }

    private List<string> SelectedPaths() =>
        _selectedAssets
            .Select(a => a.FullPath)
            .Where(p => p is not null && File.Exists(p))
            .Select(p => p!)
            .ToList();

    // ---------------------------------------------------------------- copy to clipboard / open

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyFiles()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0)
        {
            StatusText = "The selected files are not available on disk.";
            return;
        }

        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        try
        {
            Clipboard.SetFileDropList(list); // paste into Explorer, Photoshop, Discord, ...
            StatusText = $"Copied {paths.Count:N0} file(s) to the clipboard";
        }
        catch (COMException)
        {
            StatusText = "The clipboard is busy. Try again.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyPaths()
    {
        var paths = _selectedAssets.Select(a => a.FullPath).Where(p => p is not null).ToList();
        if (paths.Count == 0) return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
            StatusText = $"Copied {paths.Count:N0} path(s)";
        }
        catch (COMException)
        {
            StatusText = "The clipboard is busy. Try again.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowInExplorer()
    {
        var path = _selectedAssets[0].FullPath;
        if (path is null || !File.Exists(path)) return;
        TryStart("explorer.exe", $"/select,\"{path}\"");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSelected()
    {
        var path = _selectedAssets[0].FullPath;
        if (path is null || !File.Exists(path)) return;
        TryStart(path, null);
    }

    public void OpenInExplorer(string? path)
    {
        if (path is not null) TryStart(path, null);
    }

    private void TryStart(string fileName, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = fileName, Arguments = arguments ?? "", UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"Could not open: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- quick targets

    [RelayCommand]
    private async Task AddTargetAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a target folder (for example your Godot project)" };
        if (dialog.ShowDialog() != true) return;

        var path = Path.TrimEndingDirectorySeparator(dialog.FolderName);
        var godotRoot = GodotProject.FindRoot(path);
        var isGodot = godotRoot is not null;

        var target = new QuickTarget(
            0, GodotProject.DisplayName(path, godotRoot), path, isGodot,
            SortByType: isGodot, SnakeCaseNames: isGodot);

        await _library.AddQuickTargetAsync(target);
        await ReloadTargetsAsync();
        StatusText = isGodot
            ? "Godot project detected: files will be sorted by type and renamed to snake_case."
            : "Target added.";
    }

    public async Task CopySelectionToAsync(QuickTargetViewModel target)
    {
        if (IsBusy || !HasSelection) return;

        var model = target.Model;
        var plan = await _library.PlanCopyAsync(model, _selectedAssets.Select(a => a.Asset));
        if (plan.Count == 0)
        {
            StatusText = "Nothing to copy (the files are missing on disk, or only Godot import files were selected).";
            return;
        }

        var policy = ConflictPolicy.KeepBoth;
        var conflicts = plan.Count(p => p.Exists && !p.IsSameFile);
        if (conflicts > 0)
        {
            // A dialog in the view model is a shortcut; it can move behind an interface later.
            var answer = MessageBox.Show(
                $"{conflicts:N0} file(s) already exist in \"{model.Name}\".\n\n" +
                "Yes: overwrite them\nNo: keep both (new copies get a _2 suffix)\nCancel: don’t copy anything",
                "Files already exist", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            policy = answer == MessageBoxResult.Yes ? ConflictPolicy.Overwrite : ConflictPolicy.KeepBoth;
        }

        _syncCts = new CancellationTokenSource();
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        try
        {
            var progress = new Progress<int>(done =>
            {
                StatusText = $"Copying… {done:N0} / {plan.Count:N0}";
                ProgressValue = 100.0 * done / plan.Count;
            });
            var result = await _library.CopyAsync(plan, policy, progress, _syncCts.Token);
            StatusText = Describe(result, model);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Copy cancelled";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Describe(CopyResult result, QuickTarget target)
    {
        var text = $"Copied {result.Copied:N0} file(s) to {target.Name}";
        if (result.Skipped > 0) text += $", {result.Skipped:N0} skipped";
        if (result.Failed > 0) text += $", {result.Failed:N0} failed ({result.Errors[0]})";
        text += target.IsGodot && result.Copied > 0
            ? ". Godot imports them when the editor regains focus."
            : ".";
        return text;
    }

    public Task UpdateTargetAsync(QuickTargetViewModel target) => _library.UpdateQuickTargetAsync(target.Model);

    public async Task RemoveTargetAsync(QuickTargetViewModel target)
    {
        await _library.RemoveQuickTargetAsync(target.Model.Id);
        await ReloadTargetsAsync();
    }

    private async Task ReloadTargetsAsync()
    {
        var targets = await _library.GetQuickTargetsAsync();
        QuickTargets = targets.Select(t => new QuickTargetViewModel(t, this)).ToList();
    }

    // ---------------------------------------------------------------- folders

    public void OnFolderSelected(FolderNodeViewModel node)
    {
        if (_suppressFolderSelection) return;
        _selectedFolder = node;
        _ = RefreshAsync();
    }

    public async Task RescanRootAsync(FolderNodeViewModel root)
    {
        if (IsBusy || root.RootId is null || root.FullPath is null) return;
        await SyncRootAsync(root.RootId.Value, root.FullPath);
    }

    public async Task RemoveRootAsync(FolderNodeViewModel root)
    {
        if (IsBusy || root.RootId is null) return;

        var answer = MessageBox.Show(
            $"Remove \"{root.Title}\" from the library?\n\n" +
            "Your files on disk are not touched. Only the index (including tags) is deleted.",
            "Remove folder", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        await _library.RemoveFolderAsync(root.RootId.Value);
        await ReloadSidebarAsync();
        await RefreshAsync();
    }

    private async Task ReloadSidebarAsync()
    {
        try
        {
            RebuildFolders(await _library.GetFolderTreeAsync());
            await ReloadTargetsAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load folders: {ex.Message}";
        }
    }

    /// <summary>Rebuilds the tree but keeps what is expanded and selected.</summary>
    private void RebuildFolders(IReadOnlyList<FolderNode> nodes)
    {
        var previous = new Dictionary<string, FolderNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in Flatten(Folders)) previous[n.Key] = n;
        var selectedKey = _selectedFolder?.Key ?? FolderNodeViewModel.AllKey;

        _suppressFolderSelection = true;
        try
        {
            var all = FolderNodeViewModel.CreateAll(nodes.Sum(n => n.Count), this);
            var list = new List<FolderNodeViewModel> { all };
            list.AddRange(nodes.Select(n => FolderNodeViewModel.Create(n, this)));

            FolderNodeViewModel? selected = null;
            foreach (var vm in Flatten(list))
            {
                if (previous.TryGetValue(vm.Key, out var old))
                    vm.IsExpanded = old.IsExpanded;

                if (string.Equals(vm.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
                    selected = vm;
            }

            selected ??= all; // the selected folder disappeared (e.g. removed root)
            selected.IsSelected = true;
            _selectedFolder = selected;
            Folders = list;
        }
        finally
        {
            _suppressFolderSelection = false;
        }
    }

    private static IEnumerable<FolderNodeViewModel> Flatten(IEnumerable<FolderNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    // ---------------------------------------------------------------- scanning

    private bool CanStartSync() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStartSync))]
    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose an asset folder" };
        if (dialog.ShowDialog() != true) return;
        await AddFoldersAsync(new[] { dialog.FolderName });
    }

    /// <summary>Adds and scans folders (also used when folders are dropped onto the sidebar).</summary>
    public async Task AddFoldersAsync(IEnumerable<string> paths)
    {
        if (IsBusy)
        {
            StatusText = "Busy. Try again when the current job has finished.";
            return;
        }

        foreach (var path in paths)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var rootId = await _library.AddFolderAsync(full);
            await SyncRootAsync(rootId, full);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartSync))]
    private async Task RescanAsync()
    {
        var roots = await _library.GetRootsAsync();
        if (roots.Count == 0)
        {
            StatusText = "No folders yet. Use \"Add folder…\" or drop one onto the sidebar.";
            return;
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path))
            {
                StatusText = $"Folder not available: {root.Path}";
                continue;
            }
            await SyncRootAsync(root.Id, root.Path);
        }
    }

    [RelayCommand]
    private void Cancel() => _syncCts?.Cancel();

    private async Task SyncRootAsync(long rootId, string path)
    {
        _syncCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            // Created on the UI thread, so reports are delivered back on the UI thread.
            var progress = new Progress<LibraryProgress>(OnProgress);
            await _library.SyncAsync(rootId, path, progress, _syncCts.Token);
            StatusText = "Done";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        await ReloadSidebarAsync();
        await RefreshAsync();
    }

    private void OnProgress(LibraryProgress p)
    {
        switch (p.Phase)
        {
            case SyncPhase.Scanning:
                StatusText = $"Scanning… {p.Completed:N0} files";
                IsProgressIndeterminate = true;
                break;
            case SyncPhase.Hashing:
                StatusText = p.Total > 0 ? $"Hashing… {p.Completed:N0} / {p.Total:N0}" : "Hashing…";
                SetDeterminate(p);
                break;
            case SyncPhase.Thumbnails:
                StatusText = p.Total > 0 ? $"Thumbnails… {p.Completed:N0} / {p.Total:N0}" : "Thumbnails…";
                SetDeterminate(p);
                break;
        }

        // The scan is finished once hashing starts: show the files right away (with placeholders).
        if (p.Phase != _lastPhase)
        {
            _lastPhase = p.Phase;
            if (p.Phase == SyncPhase.Hashing)
            {
                _ = ReloadSidebarAsync();
                _ = RefreshAsync();
            }
        }
    }

    private void SetDeterminate(LibraryProgress p)
    {
        IsProgressIndeterminate = p.Total == 0;
        ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Completed / p.Total;
    }

    // ---------------------------------------------------------------- search

    private async Task RefreshAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        try
        {
            await Task.Delay(150, cts.Token); // debounce while typing

            var assets = await _library.SearchAsync(new AssetQuery
            {
                Text = SearchText,
                Type = SelectedType.Type,
                RootId = _selectedFolder?.RootId,
                Folder = _selectedFolder?.Folder,
                Limit = 50_000,
            }, cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            // One assignment = one UI update (much cheaper than adding thousands of items one by one).
            Items = assets.Select(a => new AssetItemViewModel(a, _library)).ToList();
            ResultCount = Items.Count;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer search
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
        }
    }
}
