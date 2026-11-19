namespace AssetManager.Core.Models;

/// <summary>A saved destination folder for one-click copying (e.g. a folder inside a Godot project).</summary>
/// <param name="IsGodot">The folder is inside a Godot project (a parent folder contains project.godot).</param>
/// <param name="SortByType">Copy into per-type subfolders (textures/, models/, audio/, ...).</param>
/// <param name="SnakeCaseNames">Rename files to snake_case while copying.</param>
public sealed record QuickTarget(
    long Id, string Name, string Path, bool IsGodot, bool SortByType, bool SnakeCaseNames);
