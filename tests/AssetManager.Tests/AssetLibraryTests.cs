using AssetManager.Core.Models;
using AssetManager.Core.Services;
using ImageMagick;
using Microsoft.Data.Sqlite;

namespace AssetManager.Tests;

public sealed class AssetLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "am-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Sync_runs_scan_hash_and_thumbnails_end_to_end()
    {
        var lib = Path.Combine(_dir, "library");
        Directory.CreateDirectory(lib);
        using (var image = new MagickImage(MagickColors.Orange, 400, 200))
            image.Write(Path.Combine(lib, "orange.png"));
        await File.WriteAllTextAsync(Path.Combine(lib, "model.fbx"), "not a real mesh");

        var library = await AssetLibrary.OpenAsync(Path.Combine(_dir, "t.db"), Path.Combine(_dir, "thumbs"));
        var rootId = await library.AddFolderAsync(lib);

        var phases = new List<SyncPhase>();
        await library.SyncAsync(rootId, lib, new Progress<LibraryProgress>(p => phases.Add(p.Phase)));

        var assets = await library.SearchAsync(new AssetQuery());
        Assert.Equal(2, assets.Count);

        var png = assets.Single(a => a.FileName == "orange.png");
        Assert.Equal(400, png.Width);
        Assert.NotNull(library.GetThumbnailPath(png));
        Assert.True(File.Exists(library.GetThumbnailPath(png)));

        var fbx = assets.Single(a => a.FileName == "model.fbx");
        Assert.Equal(AssetType.Mesh, fbx.Type);
        Assert.Null(library.GetThumbnailPath(fbx));

        Assert.Single(await library.GetRootsAsync());
    }
}
