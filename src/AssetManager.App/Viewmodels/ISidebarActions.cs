namespace AssetManager.App.ViewModels;

/// <summary>What sidebar items (folders, tags, bundles and quick targets) can ask the main view model to do.</summary>
public interface ISidebarActions
{
    bool HasSelection { get; }

    void OnFolderSelected(FolderNodeViewModel node);
    Task RescanRootAsync(FolderNodeViewModel root);
    Task RemoveRootAsync(FolderNodeViewModel root);
    Task MarkFolderOwnAsync(FolderNodeViewModel folder, bool value);
    Task DeleteTagAsync(FolderNodeViewModel tag);
    void OpenInExplorer(string? path);

    Task NewBundleAsync();
    Task RenameBundleAsync(FolderNodeViewModel bundle);
    Task DeleteBundleAsync(FolderNodeViewModel bundle);
    Task ExportBundleAsync(FolderNodeViewModel bundle);

    Task CopySelectionToAsync(QuickTargetViewModel target);
    Task UpdateTargetAsync(QuickTargetViewModel target);
    Task RemoveTargetAsync(QuickTargetViewModel target);
}
