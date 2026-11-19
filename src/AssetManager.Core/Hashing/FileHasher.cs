using System.Buffers;
using System.IO.Hashing;

namespace AssetManager.Core.Hashing;

public static class FileHasher
{
    private const int BufferSize = 1 << 20; // 1 MB reads keep large textures fast

    /// <summary>
    /// 128-bit xxHash of the file's bytes as lower-case hex. Fast and collision-resistant enough for
    /// finding duplicates among your own files; not suitable for security purposes.
    /// </summary>
    public static async Task<string> ComputeHashAsync(string path, CancellationToken ct = default)
    {
        // ReadWrite|Delete sharing so a file open in another program (e.g. Blender) can still be hashed.
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
                hasher.Append(buffer.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(hasher.GetCurrentHash()).ToLowerInvariant();
    }
}
