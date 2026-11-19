namespace AssetManager.Core.Services;

public static class GodotProject
{
    /// <summary>Walks up from <paramref name="folder"/> looking for project.godot. Returns the project root, or null.</summary>
    public static string? FindRoot(string folder)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "project.godot")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>"MyGame" for the project root, "MyGame/assets/textures" for a folder inside it.</summary>
    public static string DisplayName(string folder, string? projectRoot)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(folder);
        var name = Path.GetFileName(trimmed);
        if (name.Length == 0) name = folder;
        if (projectRoot is null) return name;

        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectRoot));
        var relative = Path.GetRelativePath(projectRoot, trimmed);
        return relative == "." ? rootName : $"{rootName}/{relative.Replace('\\', '/')}";
    }
}
