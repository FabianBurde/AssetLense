using System.IO.Compression;
using AssetManager.Core.Bundling;
using AssetManager.Core.Database;
using AssetManager.Core.Licensing;
using AssetManager.Core.Models;
using Microsoft.Data.Sqlite;

namespace AssetManager.Tests;

public sealed class BundleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "am-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _lib;
    private readonly AssetRepository _repo;
    private long _nextId = 1;

    public BundleTests()
    {
        _lib = Path.Combine(_dir, "library");
        Directory.CreateDirectory(_lib);
        var db = new AssetDatabase(Path.Combine(_dir, "test.db"));
        db.InitializeAsync().GetAwaiter().GetResult();
        _repo = new AssetRepository(db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private ExportItem Item(string relative, long rootId = 1, string rootName = "lib", string? rootPath = null,
        LicenseResolution? license = null, ReadmeResolution? readme = null, bool own = false)
    {
        rootPath ??= _lib;
        var asset = Asset.Create(rootId, relative, 1, T0) with { Id = _nextId++, IsOwn = own };
        var full = Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar));
        return new ExportItem(asset, rootName, rootPath, full, license, readme);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_lib, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static LicenseResolution License(string id, string folder = "packs", string? file = "packs/LICENSE.txt") =>
        new(LicenseCatalog.Get(id), 1, folder, file, false, false);

    // ---------- bundles in the database ----------

    [Fact]
    public async Task Bundles_can_be_created_filled_filtered_renamed_and_deleted()
    {
        var root = await _repo.AddRootAsync(_lib);
        await _repo.UpsertManyAsync(new[]
        {
            Asset.Create(root, "a.png", 1, T0), Asset.Create(root, "b.png", 1, T0), Asset.Create(root, "c.png", 1, T0),
        });
        var assets = await _repo.SearchAsync(new AssetQuery());
        var a = assets.Single(x => x.FileName == "a.png");
        var b = assets.Single(x => x.FileName == "b.png");

        var bundle = await _repo.CreateBundleAsync("  Godot   starter ");
        Assert.Equal("Godot starter", bundle.Name);
        Assert.Equal(bundle.Id, (await _repo.CreateBundleAsync("godot STARTER")).Id); // same name, same bundle

        Assert.Equal(2, await _repo.AddToBundleAsync(bundle.Id, new[] { a.Id, b.Id }));
        Assert.Equal(0, await _repo.AddToBundleAsync(bundle.Id, new[] { a.Id }));    // already in it

        var inBundle = await _repo.SearchAsync(new AssetQuery { BundleId = bundle.Id });
        Assert.Equal(new[] { "a.png", "b.png" }, inBundle.Select(x => x.FileName).OrderBy(n => n));
        Assert.Equal(2, (await _repo.SearchAsync(new AssetQuery { AnyBundle = true })).Count);

        Assert.Equal(2, (await _repo.GetBundlesAsync()).Single().Count);
        Assert.Equal(2, (await _repo.GetFilterCountsAsync()).Bundled);

        var other = await _repo.CreateBundleAsync("Other");
        Assert.False(await _repo.RenameBundleAsync(other.Id, "godot starter")); // name taken
        Assert.True(await _repo.RenameBundleAsync(other.Id, "Forest scene"));

        Assert.Equal(1, await _repo.RemoveFromBundleAsync(bundle.Id, new[] { b.Id }));
        Assert.Single(await _repo.SearchAsync(new AssetQuery { BundleId = bundle.Id }));

        await _repo.DeleteBundleAsync(bundle.Id);
        Assert.DoesNotContain(await _repo.GetBundlesAsync(), x => x.Id == bundle.Id);
        Assert.Equal(3, (await _repo.SearchAsync(new AssetQuery())).Count); // assets are untouched
    }

    // ---------- zip planning ----------

    [Fact]
    public void Layouts_decide_the_entry_names_and_collisions_get_a_suffix()
    {
        var items = new[]
        {
            Item("tex/Rock Wall.png"),
            Item("mesh/hero.glb"),
            Item("other/Rock Wall.png"),
        };

        var keep = ZipPlanner.PlanFiles(items, new BundleExportOptions());
        Assert.Equal(new[] { "tex/Rock Wall.png", "mesh/hero.glb", "other/Rock Wall.png" }, keep.Select(p => p.EntryName));

        var flat = ZipPlanner.PlanFiles(items, new BundleExportOptions(ZipLayout.Flat));
        Assert.Equal(new[] { "Rock Wall.png", "hero.glb", "Rock Wall_2.png" }, flat.Select(p => p.EntryName));

        var byType = ZipPlanner.PlanFiles(items, new BundleExportOptions(ZipLayout.ByType, SnakeCaseNames: true));
        Assert.Equal(new[] { "textures/rock_wall.png", "models/hero.glb", "textures/rock_wall_2.png" }, byType.Select(p => p.EntryName));
    }

    [Fact]
    public void Assets_from_several_roots_are_prefixed_with_the_root_name()
    {
        var items = new[]
        {
            Item("tex/a.png", rootId: 1, rootName: "Kenney"),
            Item("tex/a.png", rootId: 2, rootName: "My: Assets"),
        };

        var plan = ZipPlanner.PlanFiles(items, new BundleExportOptions());

        Assert.Equal("Kenney/tex/a.png", plan[0].EntryName);
        Assert.Equal("My_ Assets/tex/a.png", plan[1].EntryName); // characters Windows can't use in names are replaced
    }

    // ---------- license check ----------

    [Fact]
    public void License_report_flags_missing_restricted_and_unknown_licenses_but_not_own_work()
    {
        var items = new[]
        {
            Item("own.png", own: true),
            Item("none.png"),
            Item("nc.png", license: License("CC-BY-NC-4.0")),
            Item("custom.png", license: License("Custom")),
            Item("free.png", license: License("CC0-1.0")),
            Item("by.png", license: License("CC-BY-4.0")),
        };

        var report = BundleLicenseCheck.Analyze(items);

        Assert.True(report.HasIssues);
        Assert.Equal(3, report.Issues.Count);
        Assert.All(report.Issues, i => Assert.Equal(1, i.Count));
        Assert.Equal("none.png", report.Issues.Single(i => i.Category == "no license").Examples.Single());

        Assert.False(BundleLicenseCheck.Analyze(new[] { items[0], items[4], items[5] }).HasIssues);
    }

    // ---------- writing the zip ----------

    [Fact]
    public async Task Export_writes_files_licenses_and_credits_and_leaves_no_temp_file()
    {
        Write("packs/LICENSE.txt", "Creative Commons Attribution 4.0");
        Write("packs/README.md", "Pack by Bob");
        Write("packs/tex/a.png", "AAAA");
        Write("mine/b.png", "BBBB");

        var license = License("CC-BY-4.0");
        var readme = new ReadmeResolution(1, "packs", "packs/README.md");
        var items = new[]
        {
            Item("packs/tex/a.png", license: license, readme: readme),
            Item("mine/b.png", own: true),
            Item("packs/tex/missing.png", license: license),
        };

        var zipPath = Path.Combine(_dir, "out", "bundle.zip");
        var result = await BundleExporter.ExportAsync(items, zipPath, new BundleExportOptions());

        Assert.Equal(2, result.Files);
        Assert.Single(result.Skipped);                              // the file that doesn't exist
        Assert.True(File.Exists(zipPath));
        Assert.False(File.Exists(zipPath + ".tmp"));

        using var archive = ZipFile.OpenRead(zipPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("packs/tex/a.png", names);
        Assert.Contains("mine/b.png", names);
        Assert.DoesNotContain("packs/tex/missing.png", names);
        Assert.Contains("licenses/packs/LICENSE.txt", names);
        Assert.Contains("licenses/packs/README.md", names);
        Assert.Contains("CREDITS.txt", names);

        using var reader = new StreamReader(archive.GetEntry("CREDITS.txt")!.Open());
        var credits = reader.ReadToEnd();
        Assert.Contains("packs/tex/a.png", credits);
        Assert.Contains("CC BY 4.0", credits);
        Assert.Contains("licenses/packs/LICENSE.txt", credits);
        Assert.Contains("Own work", credits);
        Assert.DoesNotContain("missing.png", credits);

        using var entryReader = new StreamReader(archive.GetEntry("packs/tex/a.png")!.Open());
        Assert.Equal("AAAA", entryReader.ReadToEnd());
    }

    [Fact]
    public async Task Options_can_leave_out_license_files_and_credits()
    {
        Write("packs/LICENSE.txt", "x");
        Write("packs/a.png", "AAAA");
        var items = new[] { Item("packs/a.png", license: License("MIT")) };
        var zipPath = Path.Combine(_dir, "plain.zip");

        await BundleExporter.ExportAsync(items, zipPath,
            new BundleExportOptions(IncludeLicenseFiles: false, IncludeCredits: false));

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Equal(new[] { "packs/a.png" }, archive.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task A_cancelled_export_leaves_nothing_behind()
    {
        Write("a.png", "AAAA");
        var items = new[] { Item("a.png") };
        var zipPath = Path.Combine(_dir, "cancelled.zip");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BundleExporter.ExportAsync(items, zipPath, new BundleExportOptions(), null, cts.Token));

        Assert.False(File.Exists(zipPath));
        Assert.False(File.Exists(zipPath + ".tmp"));
    }
}
