using AssetManager.Core.Database;
using AssetManager.Core.Hashing;
using AssetManager.Core.Models;
using AssetManager.Core.Scanning;
using AssetManager.Core.Thumbnails;
using ImageMagick;
using Microsoft.Data.Sqlite;

namespace AssetManager.Tests;

public sealed class ThumbnailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "am-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _lib;
    private readonly AssetRepository _repo;
    private readonly FolderScanner _scanner;
    private readonly HashingService _hashing;
    private readonly ThumbnailCache _cache;
    private readonly MagickThumbnailGenerator _generator = new();
    private readonly ThumbnailService _service;

    public ThumbnailTests()
    {
        _lib = Path.Combine(_dir, "library");
        Directory.CreateDirectory(_lib);

        var db = new AssetDatabase(Path.Combine(_dir, "test.db"));
        db.InitializeAsync().GetAwaiter().GetResult();
        _repo = new AssetRepository(db);
        _scanner = new FolderScanner(_repo);
        _hashing = new HashingService(_repo);
        _cache = new ThumbnailCache(Path.Combine(_dir, "thumbs"));
        _service = new ThumbnailService(_repo, _generator, _cache);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string CreatePng(string relative, int width, int height)
    {
        var path = Path.Combine(_lib, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new MagickImage(MagickColors.CornflowerBlue, (uint)width, (uint)height);
        image.Write(path);
        return path;
    }

    [Fact]
    public async Task Large_image_is_scaled_down_keeping_aspect_ratio()
    {
        var path = CreatePng("big.png", 600, 300);

        var result = await _generator.GenerateAsync(path, 256);

        Assert.NotNull(result);
        Assert.Equal(600, result.SourceWidth);
        Assert.Equal(300, result.SourceHeight);
        using var thumb = new MagickImage(result.PngData);
        Assert.Equal(256u, thumb.Width);
        Assert.Equal(128u, thumb.Height);
    }

    [Fact]
    public async Task Small_image_is_not_upscaled()
    {
        var path = CreatePng("small.png", 100, 50);

        var result = await _generator.GenerateAsync(path, 256);

        Assert.NotNull(result);
        using var thumb = new MagickImage(result.PngData);
        Assert.Equal(100u, thumb.Width);
    }

    [Fact]
    public async Task Corrupt_file_returns_null()
    {
        var path = Path.Combine(_lib, "bad.png");
        await File.WriteAllTextAsync(path, "this is not an image");

        Assert.Null(await _generator.GenerateAsync(path, 256));
    }

    [Fact]
    public async Task Pipeline_creates_cache_files_records_dimensions_and_skips_non_textures()
    {
        CreatePng("tex/a.png", 600, 300);
        await File.WriteAllTextAsync(Path.Combine(_lib, "tex", "bad.png"), "not an image");
        await File.WriteAllTextAsync(Path.Combine(_lib, "notes.txt"), "hello");

        var root = await _repo.AddRootAsync(_lib);
        await _scanner.ScanAsync(root, _lib);
        await _hashing.HashPendingAsync(root);

        var run = await _service.GeneratePendingAsync(root);
        Assert.Equal(2, run.Completed);   // a.png and bad.png; notes.txt isn't a texture
        Assert.Equal(1, run.Failed);

        var all = await _repo.SearchAsync(new AssetQuery());
        var good = all.Single(a => a.FileName == "a.png");
        Assert.Equal(600, good.Width);
        Assert.Equal(300, good.Height);
        Assert.True(_cache.Exists(good.ContentHash!));

        Assert.Equal(0, all.Single(a => a.FileName == "bad.png").Width); // undecodable sentinel
        Assert.Null(all.Single(a => a.FileName == "notes.txt").Width);

        var again = await _service.GeneratePendingAsync(root);
        Assert.Equal(0, again.Total);
    }

    [Fact]
    public async Task Reset_makes_everything_pending_again()
    {
        CreatePng("a.png", 64, 64);
        var root = await _repo.AddRootAsync(_lib);
        await _scanner.ScanAsync(root, _lib);
        await _hashing.HashPendingAsync(root);
        await _service.GeneratePendingAsync(root);

        Assert.Equal(1, await _repo.ResetThumbnailsAsync());
        Assert.Equal(1, await _repo.CountPendingThumbnailsAsync(root));
    }
}
