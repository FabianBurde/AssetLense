using System.Collections.Concurrent;
using AssetManager.Core.Database;

namespace AssetManager.Core.Hashing;

public sealed record HashProgress(int Completed, int Total, int Failed);

/// <summary>Hashes every indexed file that doesn't have a hash yet.</summary>
public sealed class HashingService
{
    private const int ChunkSize = 500;
    private readonly AssetRepository _repo;

    public HashingService(AssetRepository repo) => _repo = repo;

    /// <param name="maxParallelism">4 suits SSDs; use 1-2 for spinning disks or network drives.</param>
    public async Task<HashProgress> HashPendingAsync(
        long? rootId = null,
        int maxParallelism = 4,
        IProgress<HashProgress>? progress = null,
        CancellationToken ct = default)
    {
        var total = await _repo.CountPendingHashesAsync(rootId, ct);
        var completed = 0;
        var failed = 0;
        long afterId = 0;

        while (true)
        {
            var chunk = await _repo.GetPendingHashesAsync(afterId, ChunkSize, rootId, ct);
            if (chunk.Count == 0) break;
            afterId = chunk[^1].Id; // keyset paging: files that fail to hash are skipped, never retried in a loop

            var results = new ConcurrentBag<(long Id, string Hash)>();
            try
            {
                await Parallel.ForEachAsync(
                    chunk,
                    new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct },
                    async (candidate, token) =>
                    {
                        try
                        {
                            results.Add((candidate.Id, await FileHasher.ComputeHashAsync(candidate.FullPath, token)));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            Interlocked.Increment(ref failed); // locked/deleted; a later scan will flag it if it's gone
                        }

                        var done = Interlocked.Increment(ref completed);
                        if (done % 25 == 0 || done == total)
                            progress?.Report(new HashProgress(done, total, Volatile.Read(ref failed)));
                    });
            }
            finally
            {
                // Save whatever finished, even if the run was cancelled mid-chunk.
                await _repo.SetHashesAsync(results, CancellationToken.None);
            }
        }

        return new HashProgress(completed, total, failed);
    }
}
