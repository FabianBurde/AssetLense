namespace AssetManager.Core.Thumbnails;

/// <summary>PNG-encoded thumbnail plus the source image's real dimensions.</summary>
public sealed record ThumbnailResult(byte[] PngData, int SourceWidth, int SourceHeight);

/// <summary>
/// Abstraction over the imaging library (Magick.NET for now), so it can be swapped
/// without touching the database, scanner or UI.
/// </summary>
public interface IThumbnailGenerator
{
    /// <summary>Returns null if the file can't be decoded.</summary>
    Task<ThumbnailResult?> GenerateAsync(string filePath, int maxSize, CancellationToken ct = default);
}
