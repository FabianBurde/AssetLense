using AssetManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AssetManager.App.ViewModels;

/// <summary>One row in the sidebar tree: "All assets", a library root, or a folder inside a root.</summary>
public sealed partial class FolderNodeViewModel : ObservableObject
{
    public const string AllKey = "all";

    private readonly ISidebarActions _actions;

    private FolderNodeViewModel(
        ISidebarActions actions, long? rootId, string title, string folder, string? fullPath,
        int count, bool isRoot, bool isOffline, IReadOnlyList<FolderNodeViewModel> children)
    {
        _actions = actions;
        RootId = rootId;
        Title = title;
        Folder = folder;
        FullPath = fullPath;
        Count = count;
        IsRoot = isRoot;
        IsOffline = isOffline;
        Children = children;
    }

    public static FolderNodeViewModel CreateAll(int totalCount, ISidebarActions actions) =>
        new(actions, null, "All assets", "", null, totalCount, false, false, Array.Empty<FolderNodeViewModel>());

    public static FolderNodeViewModel Create(FolderNode node, ISidebarActions actions)
    {
        var children = node.Children.Select(c => Create(c, actions)).ToList();
        return new FolderNodeViewModel(
            actions, node.RootId, node.Name, node.Folder, node.FullPath,
            node.Count, node.IsRoot, node.IsOffline, children)
        {
            IsExpanded = node.IsRoot, // show the first level of every root by default
        };
    }

    public long? RootId { get; }
    public string Title { get; }

    /// <summary>Relative to the root, '/' separated; empty for the root itself.</summary>
    public string Folder { get; }
    public string? FullPath { get; }
    public int Count { get; }
    public bool IsRoot { get; }
    public bool IsOffline { get; }
    public IReadOnlyList<FolderNodeViewModel> Children { get; }

    public string DisplayText => IsOffline ? $"{Title} (offline)" : Title;
    public string Key => RootId is null ? AllKey : $"{RootId}:{Folder}";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _actions.OnFolderSelected(this);
    }

    private bool CanRescan => IsRoot && RootId is not null && !IsOffline;
    private bool HasPath => FullPath is not null;
    private bool CanRemove => IsRoot && RootId is not null;

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => _actions.RescanRootAsync(this);

    [RelayCommand(CanExecute = nameof(HasPath))]
    private void OpenInExplorer() => _actions.OpenInExplorer(FullPath);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync() => _actions.RemoveRootAsync(this);
}
