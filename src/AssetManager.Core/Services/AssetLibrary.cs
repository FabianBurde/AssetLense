using AssetManager.Core.Bundling;
using AssetManager.Core.Database;
using AssetManager.Core.Hashing;
using AssetManager.Core.Licensing;
using AssetManager.Core.Models;
using AssetManager.Core.Scanning;
using AssetManager.Core.Thumbnails;

namespace AssetManager.Core.Services;

public enum SyncPhase { Scanning, Hashing, Licenses, Thumbnails, Done }

/// <param name="Completed">Items processed in this phase.</param>
/// <param name="Total">Items in this phase, or 0 if not known yet (scanning).</param>
public sealed record LibraryProgress(SyncPhase Phase, int Completed, int Total);

public sealed record SidebarData(IReadOnlyList<FolderNode> Tree, LibraryCounts Counts, IReadOnlyList<TagInfo> Tags, IReadOnlyList<BundleInfo> Bundles);

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
    private readonly LicenseScanner _licenses;

    // Root id -> folder path, so tiles can resolve full file paths without a database hit.
    private volatile IReadOnlyDictionary<long, LibraryRoot> _roots = new Dictionary<long, LibraryRoot>();
    private volatile LicenseIndex _licenseIndex = LicenseIndex.Empty;

    public AssetLibrary(
        AssetRepository repo, FolderScanner scanner, HashingService hashing,
        ThumbnailService thumbnails, ThumbnailCache cache, LicenseScanner licenses)
    {
        _repo = repo;
        _scanner = scanner;
        _hashing = hashing;
        _thumbnails = thumbnails;
        _cache = cache;
        _licenses = licenses;
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
            cache,
            new LicenseScanner(repo));

        await library.GetRootsAsync();
        await library.RefreshLicensesAsync();
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
        _roots = roots.ToDictionary(r => r.Id);
        return roots;
    }

    /// <summary>Sidebar tree: roots, their subfolders and recursive file counts.</summary>
    public Task<IReadOnlyList<FolderNode>> GetFolderTreeAsync(CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var roots = await _repo.GetRootsAsync(ct);
            _roots = roots.ToDictionary(r => r.Id);
            var counts = await _repo.GetDirectoryCountsAsync(ct);
            return FolderTreeBuilder.Build(roots, counts, r => Directory.Exists(r.Path));
        }, ct);

    // ---- sidebar ----

    /// <summary>Everything the sidebar shows: folder tree, favorite/own/tag counts, and the tag list.</summary>
    public Task<SidebarData> GetSidebarAsync(CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var roots = await _repo.GetRootsAsync(ct);
            _roots = roots.ToDictionary(r => r.Id);
            var counts = await _repo.GetDirectoryCountsAsync(ct);
            var tree = FolderTreeBuilder.Build(roots, counts, r => Directory.Exists(r.Path));
            return new SidebarData(tree, await _repo.GetFilterCountsAsync(ct), await _repo.GetTagsAsync(ct), await _repo.GetBundlesAsync(ct));
        }, ct);

    // ---- favorites, own work, tags ----

    public Task SetFavoriteAsync(IReadOnlyList<long> ids, bool value, CancellationToken ct = default) =>
        Task.Run(() => _repo.SetFavoriteAsync(ids, value, ct), ct);

    public Task SetOwnAsync(IReadOnlyList<long> ids, bool value, CancellationToken ct = default) =>
        Task.Run(() => _repo.SetOwnAsync(ids, value, ct), ct);

    public Task<int> SetOwnForFolderAsync(long rootId, string folder, bool value, CancellationToken ct = default) =>
        Task.Run(() => _repo.SetOwnForFolderAsync(rootId, folder, value, ct), ct);

    public Task<IReadOnlyList<TagCount>> GetTagsForAssetsAsync(IReadOnlyList<long> ids, CancellationToken ct = default) =>
        Task.Run(() => _repo.GetTagsForAssetsAsync(ids, ct), ct);

    public Task AddTagsAsync(IReadOnlyList<long> ids, IEnumerable<string> names, CancellationToken ct = default) =>
        Task.Run(() => _repo.AddTagsAsync(ids, names, ct), ct);

    public Task RemoveTagAsync(IReadOnlyList<long> ids, string name, CancellationToken ct = default) =>
        Task.Run(() => _repo.RemoveTagAsync(ids, name, ct), ct);

    public Task DeleteTagAsync(long tagId, CancellationToken ct = default) =>
        Task.Run(() => _repo.DeleteTagAsync(tagId, ct), ct);

    // ---- bundles ----

    public Task<BundleInfo> CreateBundleAsync(string name, CancellationToken ct = default) =>
        Task.Run(() => _repo.CreateBundleAsync(name, ct), ct);

    public Task<bool> RenameBundleAsync(long bundleId, string name, CancellationToken ct = default) =>
        Task.Run(() => _repo.RenameBundleAsync(bundleId, name, ct), ct);

    public Task DeleteBundleAsync(long bundleId, CancellationToken ct = default) =>
        Task.Run(() => _repo.DeleteBundleAsync(bundleId, ct), ct);

    public Task<int> AddToBundleAsync(long bundleId, IReadOnlyList<long> assetIds, CancellationToken ct = default) =>
        Task.Run(() => _repo.AddToBundleAsync(bundleId, assetIds, ct), ct);

    public Task<int> RemoveFromBundleAsync(long bundleId, IReadOnlyList<long> assetIds, CancellationToken ct = default) =>
        Task.Run(() => _repo.RemoveFromBundleAsync(bundleId, assetIds, ct), ct);

    /// <summary>Assets that may not be safe to share: no license, a restricted one, or one we couldn't identify.</summary>
    public LicenseReport AnalyzeLicenses(IEnumerable<Asset> assets) =>
        BundleLicenseCheck.Analyze(BuildExportItems(assets));

    /// <summary>Writes the assets into a zip file (see <see cref="BundleExporter"/> for the details).</summary>
    public Task<BundleExportResult> ExportZipAsync(
        IEnumerable<Asset> assets, string destinationPath, BundleExportOptions options,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        var items = BuildExportItems(assets);
        return BundleExporter.ExportAsync(items, destinationPath, options, progress, ct);
    }

    private IReadOnlyList<ExportItem> BuildExportItems(IEnumerable<Asset> assets)
    {
        var list = new List<ExportItem>();
        foreach (var asset in assets)
        {
            if (!_roots.TryGetValue(asset.RootId, out var root)) continue;

            var full = Path.Combine(root.Path, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            list.Add(new ExportItem(
                asset, root.DisplayName, root.Path, full,
                asset.IsOwn ? null : _licenseIndex.ResolveLicense(asset.RootId, asset.Folder),
                _licenseIndex.ResolveReadme(asset.RootId, asset.Folder)));
        }
        return list;
    }

    // ---- licenses and readmes ----

    public LicenseResolution? ResolveLicense(Asset asset) => _licenseIndex.ResolveLicense(asset.RootId, asset.Folder);

    public ReadmeResolution? ResolveReadme(Asset asset) => _licenseIndex.ResolveReadme(asset.RootId, asset.Folder);

    public Task<FolderDocs?> GetFolderDocsAsync(long rootId, string folder, CancellationToken ct = default) =>
        Task.Run(() => _repo.GetFolderDocsAsync(rootId, folder, ct), ct);

    public async Task SetLicenseOverrideAsync(long rootId, string folder, string? licenseId, CancellationToken ct = default)
    {
        await Task.Run(() => _repo.SetLicenseOverrideAsync(rootId, folder, licenseId, ct), ct);
        await RefreshLicensesAsync(ct);
    }

    /// <summary>Reloads the in-memory license lookup from the database.</summary>
    public async Task RefreshLicensesAsync(CancellationToken ct = default)
    {
        var (docs, overrides) = await Task.Run(async () =>
            (await _repo.GetFolderDocSummariesAsync(ct), await _repo.GetLicenseOverridesAsync(ct)), ct);
        _licenseIndex = new LicenseIndex(docs, overrides);
    }

    // ---- assets ----

    public Task<IReadOnlyList<Asset>> SearchAsync(AssetQuery query, CancellationToken ct = default) =>
        Task.Run(() => _repo.SearchAsync(query, ct), ct);

    /// <summary>Full path of the file on disk, or null if its root is unknown.</summary>
    public string? GetFullPath(Asset asset) =>
        _roots.TryGetValue(asset.RootId, out var root)
            ? Path.Combine(root.Path, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar))
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

    /// <summary>Plans a copy for plain files (e.g. dropped from Explorer); the type comes from the extension.</summary>
    public Task<IReadOnlyList<CopyPlanItem>> PlanCopyFilesAsync(QuickTarget target, IEnumerable<string> paths)
    {
        var sources = paths
            .Where(File.Exists)
            .Select(p => new CopySource(p, AssetTypeDetector.Detect(Path.GetExtension(p))))
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

            progress?.Report(new(SyncPhase.Licenses, 0, 0));
            await _licenses.ScanRootAsync(rootId, path, ct);
            await RefreshLicensesAsync(ct);

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
