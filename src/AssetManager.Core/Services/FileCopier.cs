using System.Text;
using System.Text.RegularExpressions;
using AssetManager.Core.Models;

namespace AssetManager.Core.Services;

public enum ConflictPolicy { Overwrite, KeepBoth, Skip }

public sealed record CopySource(string Path, AssetType Type);

/// <param name="Exists">The destination file already exists (or another file in this batch maps to it).</param>
/// <param name="IsSameFile">Source and destination are the same file; nothing to do.</param>
public sealed record CopyPlanItem(string SourcePath, string DestinationPath, bool Exists, bool IsSameFile);

public sealed record CopyResult(int Copied, int Skipped, int Failed, IReadOnlyList<string> Errors);

/// <summary>Works out where each file goes for a target (type subfolders, snake_case names, conflicts).</summary>
public static partial class CopyPlanner
{
    public static IReadOnlyList<CopyPlanItem> Plan(QuickTarget target, IEnumerable<CopySource> sources)
    {
        var plan = new List<CopyPlanItem>();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var ext = Path.GetExtension(source.Path);

            // Godot generates these itself; copying them between projects only causes confusion.
            if (target.IsGodot &&
                (ext.Equals(".import", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".uid", StringComparison.OrdinalIgnoreCase)))
                continue;

            var name = Path.GetFileName(source.Path);
            if (target.SnakeCaseNames)
                name = ToSnakeCaseFileName(name);

            var folder = target.SortByType ? Path.Combine(target.Path, TypeFolderName(source.Type)) : target.Path;
            var destination = Path.Combine(folder, name);

            var sameFile = string.Equals(
                Path.GetFullPath(source.Path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);
            var duplicateInBatch = !planned.Add(destination);
            var exists = !sameFile && (duplicateInBatch || File.Exists(destination));

            plan.Add(new CopyPlanItem(source.Path, destination, exists, sameFile));
        }

        return plan;
    }

    public static string TypeFolderName(AssetType type) => type switch
    {
        AssetType.Texture => "textures",
        AssetType.Mesh => "models",
        AssetType.Material => "materials",
        AssetType.Audio => "audio",
        AssetType.Animation => "animations",
        AssetType.Shader => "shaders",
        _ => "misc",
    };

    /// <summary>"Rock Wall-01.PNG" becomes "rock_wall_01.png"; "RockWall01.png" becomes "rock_wall01.png".</summary>
    public static string ToSnakeCaseFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(fileName);

        stem = CamelBoundary().Replace(stem, "_");

        var sb = new StringBuilder(stem.Length);
        foreach (var ch in stem)
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_');

        var result = Underscores().Replace(sb.ToString(), "_").Trim('_');
        return (result.Length == 0 ? "file" : result) + ext;
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    [GeneratedRegex("_+")]
    private static partial Regex Underscores();
}

public static class FileCopier
{
    /// <summary>Copies the planned files on a worker thread. Originals are never moved or modified.</summary>
    public static Task<CopyResult> ExecuteAsync(
        IReadOnlyList<CopyPlanItem> plan, ConflictPolicy policy,
        IProgress<int>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            int copied = 0, skipped = 0, failed = 0;
            var errors = new List<string>();

            foreach (var item in plan)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var destination = item.DestinationPath;
                    var doCopy = !item.IsSameFile;

                    if (doCopy && item.Exists)
                    {
                        if (policy == ConflictPolicy.Skip) doCopy = false;
                        else if (policy == ConflictPolicy.KeepBoth) destination = UniquePath(destination);
                    }

                    if (doCopy)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(item.SourcePath, destination, overwrite: policy == ConflictPolicy.Overwrite);
                        copied++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    errors.Add($"{Path.GetFileName(item.SourcePath)}: {ex.Message}");
                }

                progress?.Report(copied + skipped + failed);
            }

            return new CopyResult(copied, skipped, failed, errors);
        }, ct);

    private static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
