namespace AssetManager.Core.Models;

public sealed record AssetQuery
{
    /// <summary>Substring match against the relative path (case-insensitive for ASCII).</summary>
    public string? Text { get; init; }
    public AssetType? Type { get; init; }

    /// <summary>Limit to one library root.</summary>
    public long? RootId { get; init; }

    /// <summary>
    /// Limit to a folder inside <see cref="RootId"/> (relative, '/' separated), including all subfolders.
    /// Null or empty means the whole root. Ignored when RootId is null.
    /// </summary>
    public string? Folder { get; init; }

    public bool FavoritesOnly { get; init; }
    public bool OwnOnly { get; init; }

    /// <summary>Only assets that carry this tag.</summary>
    public long? TagId { get; init; }

    /// <summary>Only assets that carry at least one tag.</summary>
    public bool AnyTag { get; init; }

    public int Limit { get; init; } = 200;
    public int Offset { get; init; } = 0;
}
