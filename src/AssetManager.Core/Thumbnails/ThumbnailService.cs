using System.Collections.Concurrent;
using AssetManager.Core.Database;

namespace AssetManager.Core.Thumbnails;

public sealed record ThumbnailProgress(int Completed, int Total, int Failed);

/// <summary>
/// Generates cached thumbnails for hashed textures and records their dimensions.
/// Run after hashing: scan -> hash -> thumbnails.
/// </summary>
public sealed class ThumbnailService
{
    private const int ChunkSize = 200;

    private readonly AssetRepository _repo;
    private readonly IThumbnailGenerator _generator;
    private readonly ThumbnailCache _cache;

    public ThumbnailService(AssetRepository repo, IThumbnailGenerator generator, ThumbnailCache cache)
    {
        _repo = repo;
        _generator = generator;
        _cache = cache;
    }

    /// <param name="maxParallelism">Decoding is CPU-bound; defaults to half the cores (1-4).</param>
    public async Task<ThumbnailProgress> GeneratePendingAsync(
        long? rootId = null,
        int? maxParallelism = null,
        IProgress<ThumbnailProgress>? progress = null,
        CancellationToken ct = default)
    {
        var parallelism = maxParallelism ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var total = await _repo.CountPendingThumbnailsAsync(rootId, ct);
        var completed = 0;
        var failed = 0;
        long afterId = 0;

        while (true)
        {
            var chunk = await _repo.GetPendingThumbnailsAsync(afterId, ChunkSize, rootId, ct);
            if (chunk.Count == 0) break;
            afterId = chunk[^1].Id;

            var results = new ConcurrentBag<(long Id, int Width, int Height)>();
            try
            {
                await Parallel.ForEachAsync(
                    chunk,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                    async (candidate, token) =>
                    {
                        try
                        {
                            var thumb = await _generator.GenerateAsync(candidate.FullPath, ThumbnailCache.ThumbnailSize, token);
                            if (thumb is null)
                            {
                                // Undecodable: record 0x0 so we don't retry until the file changes.
                                Interlocked.Increment(ref failed);
                                results.Add((candidate.Id, 0, 0));
                            }
                            else
                            {
                                await _cache.SaveAsync(candidate.ContentHash, thumb.PngData, token);
                                results.Add((candidate.Id, thumb.SourceWidth, thumb.SourceHeight));
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Interlocked.Increment(ref failed); // transient (locked file); left pending, retried next run
                        }

                        var done = Interlocked.Increment(ref completed);
                        if (done % 10 == 0 || done == total)
                            progress?.Report(new ThumbnailProgress(done, total, Volatile.Read(ref failed)));
                    });
            }
            finally
            {
                await _repo.SetDimensionsAsync(results, CancellationToken.None);
            }
        }

        return new ThumbnailProgress(completed, total, failed);
    }
}
