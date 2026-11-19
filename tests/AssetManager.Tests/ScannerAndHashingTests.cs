using AssetManager.Core.Database;
using AssetManager.Core.Hashing;
using AssetManager.Core.Models;
using AssetManager.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace AssetManager.Tests;

public sealed class ScannerAndHashingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "am-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _lib;
    private readonly AssetRepository _repo;
    private readonly FolderScanner _scanner;
    private readonly HashingService _hashing;

    public ScannerAndHashingTests()
    {
        _lib = Path.Combine(_dir, "library");
        Directory.CreateDirectory(_lib);

        var db = new AssetDatabase(Path.Combine(_dir, "test.db"));
        db.InitializeAsync().GetAwaiter().GetResult();
        _repo = new AssetRepository(db);
        _scanner = new FolderScanner(_repo);
        _hashing = new HashingService(_repo);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_lib, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Scan_indexes_files_and_flags_deleted_ones_as_missing()
    {
        Write("tex/a.png", "aaa");
        Write("tex/b.png", "bbb");
        Write("mesh/m.fbx", "mmm");
        var root = await _repo.AddRootAsync(_lib);

        var first = await _scanner.ScanAsync(root, _lib);
        Assert.Equal(3, first.FilesSeen);
        Assert.Equal(0, first.MarkedMissing);
        Assert.Equal(3, (await _repo.SearchAsync(new AssetQuery())).Count);
        Assert.Contains(await _repo.SearchAsync(new AssetQuery()), a => a.RelativePath == "mesh/m.fbx");

        File.Delete(Path.Combine(_lib, "tex", "b.png"));
        var second = await _scanner.ScanAsync(root, _lib);

        Assert.Equal(2, second.FilesSeen);
        Assert.Equal(1, second.MarkedMissing);
        Assert.Equal(2, (await _repo.SearchAsync(new AssetQuery())).Count);
    }

    [Fact]
    public async Task Scan_throws_for_missing_folder()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _scanner.ScanAsync(1, Path.Combine(_dir, "nope")));
    }

    [Fact]
    public async Task Hashing_finds_duplicates_and_is_a_no_op_the_second_time()
    {
        Write("a/stone.png", "same-bytes");
        Write("b/stone_copy.png", "same-bytes");
        Write("c/other.png", "different");
        var root = await _repo.AddRootAsync(_lib);
        await _scanner.ScanAsync(root, _lib);

        var run1 = await _hashing.HashPendingAsync(root);
        Assert.Equal(3, run1.Completed);
        Assert.Equal(0, run1.Failed);

        var group = Assert.Single(await _repo.FindDuplicatesAsync());
        Assert.Equal(2, group.Count());

        var run2 = await _hashing.HashPendingAsync(root);
        Assert.Equal(0, run2.Total);
    }

    [Fact]
    public async Task FileHasher_is_deterministic_and_content_sensitive()
    {
        Write("x.bin", "hello");
        Write("y.bin", "hello");
        Write("z.bin", "hellp");

        var x = await FileHasher.ComputeHashAsync(Path.Combine(_lib, "x.bin"));
        var y = await FileHasher.ComputeHashAsync(Path.Combine(_lib, "y.bin"));
        var z = await FileHasher.ComputeHashAsync(Path.Combine(_lib, "z.bin"));

        Assert.Equal(x, y);
        Assert.NotEqual(x, z);
        Assert.Equal(32, x.Length); // 128 bits as hex
    }
}
