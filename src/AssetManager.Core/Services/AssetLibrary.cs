using AssetManager.Core.Database;
using AssetManager.Core.Hashing;
using AssetManager.Core.Models;
using AssetManager.Core.Scanning;
using AssetManager.Core.Thumbnails;

namespace AssetManager.Core.Services;

public enum SyncPhase { Scanning, Hashing, Thumbnails, Done }

/// <param name="Completed">Items processed in this phase.</param>
/// <param name="Total">Items in this phase, or 0 if not known yet (scanning).</param>
public sealed record LibraryProgress(SyncPhase Phase, int Completed, int Total);

/// <summary>
/// The one API the UI talks to. Everything here runs off the calling thread, because SQLite's
/// "async" methods actually execute synchronously and would otherwise block the UI.
/// </summary>
public sealed class AssetLibrary
{
    private readonly AssetRepository _repo;
    private readonly FolderScanner _scanner;
    private readonly HashingService _hashing;
    private readonly ThumbnailService _thumbnails;
    private readonly ThumbnailCache _cache;

    // Root id -> folder path, so tiles can resolve full file paths without a database hit.
    private volatile IReadOnlyDictionary<long, string> _rootPaths = new Dictionary<long, string>();

    public AssetLibrary(
        AssetRepository repo, FolderScanner scanner, HashingService hashing,
        ThumbnailService thumbnails, ThumbnailCache cache)
    {
        _repo = repo;
        _scanner = scanner;
        _hashing = hashing;
        _thumbnails = thumbnails;
        _cache = cache;
    }

    /// <summary>Creates the default wiring (database, cache, Magick.NET) and applies migrations.</summary>
    public static async Task<AssetLibrary> OpenAsync(string? dbPath = null, string? thumbnailDirectory = null)
    {
        var db = new AssetDatabase(dbPath ?? AssetDatabase.DefaultPath);
        await Task.Run(() => db.InitializeAsync());

        var repo = new AssetRepository(db);
        var cache = new ThumbnailCache(thumbnailDirectory);
        var library = new AssetLibrary(
            repo,
            new FolderScanner(repo),
            new HashingService(repo),
            new ThumbnailService(repo, new MagickThumbnailGenerator(), cache),
            cache);

        await library.GetRootsAsync();
        return library;
    }

    // ---- folders ----

    public async Task<long> AddFolderAsync(string path, CancellationToken ct = default)
    {
        var id = await Task.Run(() => _repo.AddRootAsync(path, null, ct), ct);
        await GetRootsAsync(ct);
        return id;
    }

    public async Task RemoveFolderAsync(long rootId, CancellationToken ct = default)
    {
        await Task.Run(() => _repo.RemoveRootAsync(rootId, ct), ct);
        await GetRootsAsync(ct);
    }

    public async Task<IReadOnlyList<LibraryRoot>> GetRootsAsync(CancellationToken ct = default)
    {
        var roots = await Task.Run(() => _repo.GetRootsAsync(ct), ct);
        _rootPaths = roots.ToDictionary(r => r.Id, r => r.Path);
        return roots;
    }

    /// <summary>Sidebar tree: roots, their subfolders and recursive file counts.</summary>
    public Task<IReadOnlyList<FolderNode>> GetFolderTreeAsync(CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var roots = await _repo.GetRootsAsync(ct);
            _rootPaths = roots.ToDictionary(r => r.Id, r => r.Path);
            var counts = await _repo.GetDirectoryCountsAsync(ct);
            return FolderTreeBuilder.Build(roots, counts, r => Directory.Exists(r.Path));
        }, ct);

    // ---- assets ----

    public Task<IReadOnlyList<Asset>> SearchAsync(AssetQuery query, CancellationToken ct = default) =>
        Task.Run(() => _repo.SearchAsync(query, ct), ct);

    /// <summary>Full path of the file on disk, or null if its root is unknown.</summary>
    public string? GetFullPath(Asset asset) =>
        _rootPaths.TryGetValue(asset.RootId, out var root)
            ? Path.Combine(root, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar))
            : null;

    /// <summary>Path of the cached thumbnail, or null if there isn't one (yet).</summary>
    public string? GetThumbnailPath(Asset asset) =>
        asset.ContentHash is { } hash && _cache.Exists(hash) ? _cache.GetPath(hash) : null;

    // ---- quick targets and copying ----

    public Task<IReadOnlyList<QuickTarget>> GetQuickTargetsAsync(CancellationToken ct = default) =>
        Task.Run(() => _repo.GetQuickTargetsAsync(ct), ct);

    public Task<QuickTarget> AddQuickTargetAsync(QuickTarget target, CancellationToken ct = default) =>
        Task.Run(() => _repo.AddQuickTargetAsync(target, ct), ct);

    public Task UpdateQuickTargetAsync(QuickTarget target, CancellationToken ct = default) =>
        Task.Run(() => _repo.UpdateQuickTargetAsync(target, ct), ct);

    public Task RemoveQuickTargetAsync(long id, CancellationToken ct = default) =>
        Task.Run(() => _repo.RemoveQuickTargetAsync(id, ct), ct);

    public Task<IReadOnlyList<CopyPlanItem>> PlanCopyAsync(QuickTarget target, IEnumerable<Asset> assets)
    {
        var sources = assets
            .Select(a => (Path: GetFullPath(a), a.Type))
            .Where(x => x.Path is not null && File.Exists(x.Path))
            .Select(x => new CopySource(x.Path!, x.Type))
            .ToList();
        return Task.Run(() => CopyPlanner.Plan(target, sources));
    }

    public Task<CopyResult> CopyAsync(
        IReadOnlyList<CopyPlanItem> plan, ConflictPolicy policy,
        IProgress<int>? progress = null, CancellationToken ct = default) =>
        FileCopier.ExecuteAsync(plan, policy, progress, ct);

    // ---- sync ----

    /// <summary>Scan, then hash new/changed files, then generate thumbnails.</summary>
    public Task SyncAsync(
        long rootId, string path, IProgress<LibraryProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            progress?.Report(new(SyncPhase.Scanning, 0, 0));
            await _scanner.ScanAsync(rootId, path,
                new InlineProgress<ScanProgress>(p => progress?.Report(new(SyncPhase.Scanning, p.FilesSeen, 0))), ct);

            progress?.Report(new(SyncPhase.Hashing, 0, 0));
            await _hashing.HashPendingAsync(rootId,
                progress: new InlineProgress<HashProgress>(p => progress?.Report(new(SyncPhase.Hashing, p.Completed, p.Total))),
                ct: ct);

            progress?.Report(new(SyncPhase.Thumbnails, 0, 0));
            await _thumbnails.GeneratePendingAsync(rootId,
                progress: new InlineProgress<ThumbnailProgress>(p => progress?.Report(new(SyncPhase.Thumbnails, p.Completed, p.Total))),
                ct: ct);

            progress?.Report(new(SyncPhase.Done, 0, 0));
        }, ct);

    // Progress<T> posts to the thread pool when there is no UI context, which can reorder reports.
    // This version calls straight through, so the order is preserved for the UI's own Progress<T>.
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
