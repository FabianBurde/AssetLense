namespace AssetManager.Core.Thumbnails;

/// <summary>
/// On-disk thumbnail store keyed by content hash, so identical files share one thumbnail.
/// Layout: thumbs/256/ab/abcdef....png (two-character fan-out keeps folders small).
/// </summary>
public sealed class ThumbnailCache
{
    public const int ThumbnailSize = 256;

    private readonly string _root;

    public ThumbnailCache(string? directory = null)
    {
        _root = Path.Combine(directory ?? DefaultDirectory, ThumbnailSize.ToString());
        Directory.CreateDirectory(_root);
    }

    /// <summary>%LOCALAPPDATA%\AssetManager\thumbs</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AssetManager", "thumbs");

    public string GetPath(string contentHash) =>
        Path.Combine(_root, contentHash[..2], contentHash + ".png");

    public bool Exists(string contentHash) => File.Exists(GetPath(contentHash));

    /// <summary>Writes atomically (temp file + move) so a crash never leaves a half-written thumbnail.</summary>
    public async Task SaveAsync(string contentHash, byte[] pngData, CancellationToken ct = default)
    {
        var path = GetPath(contentHash);
        if (File.Exists(path)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, pngData, ct);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp); // another worker saved the same hash first
        }
    }
}
