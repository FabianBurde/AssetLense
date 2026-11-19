using System.Diagnostics;
using AssetManager.Core.Database;
using AssetManager.Core.Models;

namespace AssetManager.Core.Scanning;

public sealed record ScanProgress(int FilesSeen);
public sealed record ScanResult(int FilesSeen, int MarkedMissing, TimeSpan Elapsed);

/// <summary>Walks a library root, indexes every file, and flags files that have disappeared.</summary>
public sealed class FolderScanner
{
    private const int BatchSize = 1000;
    private readonly AssetRepository _repo;

    public FolderScanner(AssetRepository repo) => _repo = repo;

    public Task<ScanResult> ScanAsync(
        long rootId, string rootPath, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var rootFull = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootFull))
            throw new DirectoryNotFoundException($"Library folder not found: {rootFull}");

        // Run on a worker thread so the UI never blocks on disk enumeration.
        return Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            var scanToken = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // Default skips hidden/system files (Thumbs.db, desktop.ini, ...).
            };

            var batch = new List<Asset>(BatchSize);
            var seen = 0;

            foreach (var file in new DirectoryInfo(rootFull).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();

                batch.Add(Asset.Create(
                    rootId,
                    Path.GetRelativePath(rootFull, file.FullName),
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc)));
                seen++;

                if (batch.Count >= BatchSize)
                {
                    await _repo.UpsertManyAsync(batch, scanToken, ct);
                    batch.Clear();
                    progress?.Report(new ScanProgress(seen));
                }
            }

            if (batch.Count > 0)
                await _repo.UpsertManyAsync(batch, scanToken, ct);

            // If the drive vanished mid-scan, the enumeration would look "complete but empty".
            // Refuse to flag everything as missing in that case.
            if (!Directory.Exists(rootFull))
                throw new IOException($"Library folder became unavailable during the scan: {rootFull}");

            var missing = await _repo.MarkMissingAsync(rootId, scanToken, ct);
            progress?.Report(new ScanProgress(seen));
            return new ScanResult(seen, missing, clock.Elapsed);
        }, ct);
    }
}
