using AssetManager.Core.Models;

namespace AssetManager.Core.Services;

/// <summary>Builds the sidebar tree from the index alone (no disk access), with recursive counts.</summary>
public static class FolderTreeBuilder
{
    public static IReadOnlyList<FolderNode> Build(
        IEnumerable<LibraryRoot> roots, IEnumerable<DirectoryCount> counts, Func<LibraryRoot, bool> isOnline)
    {
        var countsByRoot = counts.ToLookup(c => c.RootId);
        var result = new List<FolderNode>();

        foreach (var root in roots)
        {
            var rootNode = new FolderNode(root.Id, root.DisplayName, "", root.Path)
            {
                IsOffline = !isOnline(root),
            };
            var byFolder = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase) { [""] = rootNode };

            foreach (var c in countsByRoot[root.Id])
                EnsureNode(root, c.Folder, byFolder, rootNode).OwnCount += c.Count;

            Finish(rootNode);
            result.Add(rootNode);
        }

        return result;
    }

    private static FolderNode EnsureNode(
        LibraryRoot root, string folder, Dictionary<string, FolderNode> map, FolderNode rootNode)
    {
        var current = rootNode;
        var path = "";

        foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = path.Length == 0 ? segment : path + "/" + segment;
            if (!map.TryGetValue(path, out var node))
            {
                node = new FolderNode(
                    root.Id, segment, path,
                    Path.Combine(root.Path, path.Replace('/', Path.DirectorySeparatorChar)))
                {
                    IsOffline = rootNode.IsOffline,
                };
                map[path] = node;
                current.Children.Add(node);
            }
            current = node;
        }

        return current;
    }

    private static int Finish(FolderNode node)
    {
        node.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var total = node.OwnCount;
        foreach (var child in node.Children)
            total += Finish(child);
        node.Count = total;
        return total;
    }
}
