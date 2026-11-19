using AssetManager.Core.Database;
using AssetManager.Core.Models;
using Microsoft.Data.Sqlite;

namespace AssetManager.Tests;

public sealed class AssetRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "am-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _db;
    private readonly AssetRepository _repo;

    public AssetRepositoryTests()
    {
        _db = new AssetDatabase(Path.Combine(_dir, "test.db"));
        _db.InitializeAsync().GetAwaiter().GetResult();
        _repo = new AssetRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // release file locks so the temp folder can be deleted
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Initialize_can_run_repeatedly()
    {
        await _db.InitializeAsync();
        await _db.InitializeAsync();
        Assert.Empty(await _repo.SearchAsync(new AssetQuery()));
    }

    [Fact]
    public async Task Upsert_updates_existing_row_instead_of_duplicating()
    {
        var root = await _repo.AddRootAsync(_dir);
        await _repo.UpsertManyAsync(new[] { Asset.Create(root, @"tex\rock.PNG", 100, T0) });
        await _repo.UpsertManyAsync(new[] { Asset.Create(root, "tex/rock.png", 250, T0.AddMinutes(1)) });

        var all = await _repo.SearchAsync(new AssetQuery());
        var asset = Assert.Single(all);
        Assert.Equal(250, asset.SizeBytes);
        Assert.Equal(AssetType.Texture, asset.Type);
        Assert.Equal(".png", asset.Extension);
    }

    [Fact]
    public async Task Hash_is_kept_when_file_unchanged_and_cleared_when_changed()
    {
        var root = await _repo.AddRootAsync(_dir);
        await _repo.UpsertManyAsync(new[] { Asset.Create(root, "a.png", 100, T0) });
        var id = (await _repo.SearchAsync(new AssetQuery())).Single().Id;
        await _repo.SetHashAsync(id, "abc123");

        await _repo.UpsertManyAsync(new[] { Asset.Create(root, "a.png", 100, T0) });
        Assert.Equal("abc123", (await _repo.SearchAsync(new AssetQuery())).Single().ContentHash);

        await _repo.UpsertManyAsync(new[] { Asset.Create(root, "a.png", 101, T0.AddSeconds(5)) });
        Assert.Null((await _repo.SearchAsync(new AssetQuery())).Single().ContentHash);
    }

    [Fact]
    public async Task Finds_duplicates_by_hash()
    {
        var root = await _repo.AddRootAsync(_dir);
        await _repo.UpsertManyAsync(new[]
        {
            Asset.Create(root, "a/stone.png", 10, T0),
            Asset.Create(root, "b/stone_copy.png", 10, T0),
            Asset.Create(root, "c/other.png", 20, T0),
        });

        var all = await _repo.SearchAsync(new AssetQuery());
        foreach (var a in all)
            await _repo.SetHashAsync(a.Id, a.FileName == "other.png" ? "h2" : "h1");

        var group = Assert.Single(await _repo.FindDuplicatesAsync());
        Assert.Equal("h1", group.Key);
        Assert.Equal(2, group.Count());
    }

    [Fact]
    public async Task Search_filters_by_text_and_type_and_escapes_wildcards()
    {
        var root = await _repo.AddRootAsync(_dir);
        await _repo.UpsertManyAsync(new[]
        {
            Asset.Create(root, "props/barrel_01.fbx", 1, T0),
            Asset.Create(root, "props/barrel_01.png", 1, T0),
            Asset.Create(root, "props/barrelX01.png", 1, T0),
        });

        Assert.Equal(2, (await _repo.SearchAsync(new AssetQuery { Text = "barrel_01" })).Count); // '_' is literal
        var meshes = await _repo.SearchAsync(new AssetQuery { Type = AssetType.Mesh });
        Assert.Equal("barrel_01.fbx", Assert.Single(meshes).FileName);
    }
}
