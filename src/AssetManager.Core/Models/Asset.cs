using AssetManager.Core.Scanning;

namespace AssetManager.Core.Models;

/// <summary>
/// One file in the library. Files stay where they are on disk; the database only indexes them.
/// A file is identified by (RootId, RelativePath), so moving a whole root folder only
/// requires updating one row in library_roots.
/// </summary>
public sealed record Asset
{
    public long Id { get; init; }
    public long RootId { get; init; }

    /// <summary>Path relative to the root, always with '/' separators.</summary>
    public required string RelativePath { get; init; }
    public required string FileName { get; init; }

    /// <summary>Lower-case, including the dot (".png").</summary>
    public required string Extension { get; init; }

    /// <summary>Directory relative to the root, '/' separated; empty for files directly in the root.</summary>
    public string Folder { get; init; } = "";

    public AssetType Type { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>Hex content hash. Null until the hashing pass has run (or after the file changed).</summary>
    public string? ContentHash { get; init; }

    /// <summary>Textures only; filled in when the thumbnail is generated.</summary>
    public int? Width { get; init; }
    public int? Height { get; init; }

    public DateTimeOffset ModifiedUtc { get; init; }
    public DateTimeOffset AddedUtc { get; init; }

    /// <summary>Set when a rescan no longer finds the file. Kept so tags survive a temporary drive disconnect.</summary>
    public bool IsMissing { get; init; }

    public static Asset Create(long rootId, string relativePath, long sizeBytes, DateTimeOffset modifiedUtc)
    {
        var normalized = relativePath.Replace('\\', '/');
        var ext = Path.GetExtension(normalized).ToLowerInvariant();
        var slash = normalized.LastIndexOf('/');
        var folder = slash < 0 ? "" : normalized[..slash];

        return new Asset
        {
            RootId = rootId,
            RelativePath = normalized,
            FileName = Path.GetFileName(normalized),
            Extension = ext,
            Folder = folder,
            Type = AssetTypeDetector.Detect(ext),
            SizeBytes = sizeBytes,
            ModifiedUtc = modifiedUtc,
            AddedUtc = DateTimeOffset.UtcNow,
        };
    }
}
