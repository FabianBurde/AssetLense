using AssetManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AssetManager.App.ViewModels;

public enum NodeKind { All, Favorites, Own, TagGroup, Tag, BundleGroup, Bundle, Folder }

/// <summary>One row in the sidebar tree: a smart filter, a tag, a bundle, a library root, or a folder inside a root.</summary>
public sealed partial class FolderNodeViewModel : ObservableObject
{
    public const string AllKey = "all";

    private readonly ISidebarActions _actions;

    private FolderNodeViewModel(
        ISidebarActions actions, NodeKind kind, string title, int count,
        IReadOnlyList<FolderNodeViewModel> children,
        long? rootId = null, string folder = "", string? fullPath = null, bool isRoot = false,
        bool isOffline = false, long? tagId = null, long? bundleId = null)
    {
        _actions = actions;
        Kind = kind;
        Title = title;
        Count = count;
        Children = children;
        RootId = rootId;
        Folder = folder;
        FullPath = fullPath;
        IsRoot = isRoot;
        IsOffline = isOffline;
        TagId = tagId;
        BundleId = bundleId;
    }

    public static FolderNodeViewModel CreateSpecial(NodeKind kind, string title, int count, ISidebarActions actions) =>
        new(actions, kind, title, count, Array.Empty<FolderNodeViewModel>());

    public static FolderNodeViewModel CreateTagGroup(int taggedCount, IReadOnlyList<TagInfo> tags, ISidebarActions actions)
    {
        var children = tags
            .Select(t => new FolderNodeViewModel(actions, NodeKind.Tag, t.Name, t.Count,
                Array.Empty<FolderNodeViewModel>(), tagId: t.Id))
            .ToList();
        return new FolderNodeViewModel(actions, NodeKind.TagGroup, "Tags", taggedCount, children) { IsExpanded = true };
    }

    public static FolderNodeViewModel CreateBundleGroup(int bundledCount, IReadOnlyList<BundleInfo> bundles, ISidebarActions actions)
    {
        var children = bundles
            .Select(b => new FolderNodeViewModel(actions, NodeKind.Bundle, b.Name, b.Count,
                Array.Empty<FolderNodeViewModel>(), bundleId: b.Id))
            .ToList();
        return new FolderNodeViewModel(actions, NodeKind.BundleGroup, "Bundles", bundledCount, children) { IsExpanded = true };
    }

    public static FolderNodeViewModel Create(FolderNode node, ISidebarActions actions)
    {
        var children = node.Children.Select(c => Create(c, actions)).ToList();
        return new FolderNodeViewModel(
            actions, NodeKind.Folder, node.Name, node.Count, children,
            node.RootId, node.Folder, node.FullPath, node.IsRoot, node.IsOffline)
        {
            IsExpanded = node.IsRoot, // show the first level of every root by default
        };
    }

    public NodeKind Kind { get; }
    public long? RootId { get; }
    public long? TagId { get; }
    public long? BundleId { get; }
    public string Title { get; }

    /// <summary>Relative to the root, '/' separated; empty for the root itself.</summary>
    public string Folder { get; }
    public string? FullPath { get; }
    public int Count { get; }
    public bool IsRoot { get; }
    public bool IsOffline { get; }
    public IReadOnlyList<FolderNodeViewModel> Children { get; }

    public bool IsFolderKind => Kind == NodeKind.Folder;
    public bool IsTagKind => Kind == NodeKind.Tag;
    public bool IsBundleKind => Kind == NodeKind.Bundle;
    public bool IsBundleGroupKind => Kind == NodeKind.BundleGroup;
    public bool HasMenu => Kind is NodeKind.Folder or NodeKind.Tag or NodeKind.Bundle or NodeKind.BundleGroup;

    public string DisplayText => IsOffline ? $"{Title} (offline)" : Title;

    public string Key => Kind switch
    {
        NodeKind.All => AllKey,
        NodeKind.Favorites => "fav",
        NodeKind.Own => "own",
        NodeKind.TagGroup => "tags",
        NodeKind.Tag => $"tag:{TagId}",
        NodeKind.BundleGroup => "bundles",
        NodeKind.Bundle => $"bundle:{BundleId}",
        _ => $"{RootId}:{Folder}",
    };

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _actions.OnFolderSelected(this);
    }

    private bool CanRescan => IsFolderKind && IsRoot && RootId is not null && !IsOffline;
    private bool HasPath => FullPath is not null;
    private bool CanRemove => IsFolderKind && IsRoot && RootId is not null;

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task RescanAsync() => _actions.RescanRootAsync(this);

    [RelayCommand(CanExecute = nameof(HasPath))]
    private void OpenInExplorer() => _actions.OpenInExplorer(FullPath);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync() => _actions.RemoveRootAsync(this);

    [RelayCommand(CanExecute = nameof(IsFolderKind))]
    private Task MarkAsMineAsync() => _actions.MarkFolderOwnAsync(this, true);

    [RelayCommand(CanExecute = nameof(IsFolderKind))]
    private Task UnmarkMineAsync() => _actions.MarkFolderOwnAsync(this, false);

    [RelayCommand(CanExecute = nameof(IsTagKind))]
    private Task DeleteTagAsync() => _actions.DeleteTagAsync(this);

    [RelayCommand(CanExecute = nameof(IsBundleGroupKind))]
    private Task NewBundleAsync() => _actions.NewBundleAsync();

    [RelayCommand(CanExecute = nameof(IsBundleKind))]
    private Task RenameBundleAsync() => _actions.RenameBundleAsync(this);

    [RelayCommand(CanExecute = nameof(IsBundleKind))]
    private Task DeleteBundleAsync() => _actions.DeleteBundleAsync(this);

    [RelayCommand(CanExecute = nameof(IsBundleKind))]
    private Task ExportBundleAsync() => _actions.ExportBundleAsync(this);
}
