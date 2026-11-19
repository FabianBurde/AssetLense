namespace AssetManager.Core.Models;

/// <summary>File count for one folder (not including subfolders), straight from the database.</summary>
public sealed record DirectoryCount(long RootId, string Folder, int Count);

/// <summary>A node in the sidebar tree: either a library root or a folder inside one.</summary>
public sealed class FolderNode(long rootId, string name, string folder, string fullPath)
{
    public long RootId { get; } = rootId;
    public string Name { get; } = name;

    /// <summary>Relative to the root, '/' separated; empty for the root itself.</summary>
    public string Folder { get; } = folder;
    public string FullPath { get; } = fullPath;

    public bool IsRoot => Folder.Length == 0;
    public bool IsOffline { get; set; }

    /// <summary>Files directly in this folder.</summary>
    public int OwnCount { get; set; }

    /// <summary>Files in this folder and all subfolders.</summary>
    public int Count { get; set; }

    public List<FolderNode> Children { get; } = new();
}
