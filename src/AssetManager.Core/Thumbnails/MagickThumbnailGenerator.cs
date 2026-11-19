using ImageMagick;

namespace AssetManager.Core.Thumbnails;

/// <summary>
/// Decodes textures with Magick.NET (PNG, JPG, TGA, DDS, PSD, EXR, HDR, ...) and produces a PNG thumbnail.
/// Only the first frame is read, so mipmapped DDS and layered PSD files stay cheap.
/// </summary>
public sealed class MagickThumbnailGenerator : IThumbnailGenerator
{
    public Task<ThumbnailResult?> GenerateAsync(string filePath, int maxSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run<ThumbnailResult?>(() => Generate(filePath, maxSize), ct);
    }

    private static ThumbnailResult? Generate(string filePath, int maxSize)
    {
        try
        {
            var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };

            // TGA has no magic number, so a stream can't be sniffed; give ImageMagick a hint.
            if (string.Equals(Path.GetExtension(filePath), ".tga", StringComparison.OrdinalIgnoreCase))
                settings.Format = MagickFormat.Tga;

            // Reading from a stream (not a path) avoids ImageMagick treating "[", ":" or "@" in file names
            // as special syntax, and lets us share the file with other programs.
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 16, FileOptions.SequentialScan);
            using var image = new MagickImage(stream, settings);

            image.AutoOrient();
            var sourceWidth = (int)image.Width;
            var sourceHeight = (int)image.Height;

            // Greater = only ever shrink; never upscale small textures.
            image.Resize(new MagickGeometry((uint)maxSize, (uint)maxSize) { Greater = true });
            image.Strip();
            image.Format = MagickFormat.Png;

            return new ThumbnailResult(image.ToByteArray(), sourceWidth, sourceHeight);
        }
        catch (MagickException)
        {
            return null; // unsupported or corrupt file
        }
    }
}
