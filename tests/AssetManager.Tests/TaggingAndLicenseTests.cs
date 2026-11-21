using AssetManager.Core.Database;
using AssetManager.Core.Licensing;
using AssetManager.Core.Models;
using AssetManager.Core.Scanning;
using Microsoft.Data.Sqlite;

namespace AssetManager.Tests;

public sealed class TaggingAndLicenseTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "am-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _lib;
    private readonly AssetRepository _repo;

    public TaggingAndLicenseTests()
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

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_lib, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<(long Root, IReadOnlyList<Asset> Assets)> SeedAsync(params string[] paths)
    {
        var root = await _repo.AddRootAsync(_lib);
        await _repo.UpsertManyAsync(paths.Select(p => Asset.Create(root, p, 1, T0)).ToList());
        return (root, await _repo.SearchAsync(new AssetQuery { Limit = 1000 }));
    }

    // ---------- favorites and own ----------

    [Fact]
    public async Task Favorites_and_own_flags_are_filterable_and_survive_rescans()
    {
        var (root, assets) = await SeedAsync("a.png", "b.png", "c.png");
        var a = assets.Single(x => x.FileName == "a.png");
        var b = assets.Single(x => x.FileName == "b.png");

        await _repo.SetFavoriteAsync(new[] { a.Id, b.Id }, true);
        await _repo.SetOwnAsync(new[] { b.Id }, true);

        Assert.Equal(2, (await _repo.SearchAsync(new AssetQuery { FavoritesOnly = true })).Count);
        Assert.Equal("b.png", Assert.Single(await _repo.SearchAsync(new AssetQuery { OwnOnly = true })).FileName);

        // A rescan upserts the same rows and must not reset the flags.
        await _repo.UpsertManyAsync(new[] { Asset.Create(root, "a.png", 1, T0) });
        Assert.True((await _repo.SearchAsync(new AssetQuery { FavoritesOnly = true })).Any(x => x.FileName == "a.png"));

        await _repo.SetFavoriteAsync(new[] { a.Id }, false);
        Assert.Single(await _repo.SearchAsync(new AssetQuery { FavoritesOnly = true }));

        var counts = await _repo.GetFilterCountsAsync();
        Assert.Equal(1, counts.Favorites);
        Assert.Equal(1, counts.Own);
    }

    [Fact]
    public async Task Marking_a_folder_as_own_covers_subfolders_only()
    {
        var (root, _) = await SeedAsync("mine/a.png", "mine/deep/b.png", "mine_not/c.png", "other/d.png");

        var changed = await _repo.SetOwnForFolderAsync(root, "mine", true);

        Assert.Equal(2, changed);
        var own = await _repo.SearchAsync(new AssetQuery { OwnOnly = true });
        Assert.Equal(new[] { "a.png", "b.png" }, own.Select(a => a.FileName).OrderBy(n => n));

        await _repo.SetOwnForFolderAsync(root, "mine", false);
        Assert.Empty(await _repo.SearchAsync(new AssetQuery { OwnOnly = true }));
    }

    // ---------- tags ----------

    [Fact]
    public async Task Tags_can_be_added_filtered_searched_and_removed()
    {
        var (_, assets) = await SeedAsync("rock.png", "wood.png", "metal.png");
        var rock = assets.Single(a => a.FileName == "rock.png");
        var wood = assets.Single(a => a.FileName == "wood.png");

        await _repo.AddTagsAsync(new[] { rock.Id, wood.Id }, new[] { "Tileable", "  rough   surface " });
        await _repo.AddTagsAsync(new[] { rock.Id }, new[] { "tileable" }); // same tag, different case

        var tags = await _repo.GetTagsAsync();
        Assert.Equal(2, tags.Count);
        var tileable = tags.Single(t => t.Name.Equals("Tileable", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, tileable.Count);
        Assert.Contains(tags, t => t.Name == "rough surface");

        Assert.Equal(2, (await _repo.SearchAsync(new AssetQuery { TagId = tileable.Id })).Count);
        Assert.Equal(2, (await _repo.SearchAsync(new AssetQuery { AnyTag = true })).Count);

        // typing a tag name in the search box finds tagged assets even if the path doesn't match
        var byText = await _repo.SearchAsync(new AssetQuery { Text = "rough" });
        Assert.Equal(2, byText.Count);

        var forSelection = await _repo.GetTagsForAssetsAsync(new[] { rock.Id, wood.Id, assets.Single(a => a.FileName == "metal.png").Id });
        Assert.Equal(2, forSelection.Count);                       // both tags present
        Assert.All(forSelection, t => Assert.Equal(2, t.Count));   // each on 2 of the 3 selected

        await _repo.RemoveTagAsync(new[] { rock.Id }, "TILEABLE");
        Assert.Equal(1, (await _repo.GetTagsAsync()).Single(t => t.Id == tileable.Id).Count);

        await _repo.DeleteTagAsync(tileable.Id);
        Assert.DoesNotContain(await _repo.GetTagsAsync(), t => t.Id == tileable.Id);
    }

    [Fact]
    public void Tag_input_is_split_and_normalized()
    {
        Assert.Equal(new[] { "wood", "tile able" }, TagNames.Split(" wood, tile   able ;; WOOD "));
        Assert.Empty(TagNames.Split("  , ; "));
    }

    // ---------- license detection ----------

    [Theory]
    [InlineData("Creative Commons Zero, public domain dedication.\nCC0 1.0 Universal", "CC0-1.0")]
    [InlineData("License: CC-BY 4.0 - please credit Kenney", "CC-BY-4.0")]
    [InlineData("Licensed under CC BY-SA 4.0", "CC-BY-SA-4.0")]
    [InlineData("See https://creativecommons.org/licenses/by-nc-sa/3.0/ for details", "CC-BY-NC-SA-3.0")]
    [InlineData("Creative Commons Attribution-NonCommercial 4.0 International", "CC-BY-NC-4.0")]
    [InlineData("Permission is hereby granted, free of charge, to any person obtaining a copy of this software", "MIT")]
    [InlineData("Apache License\nVersion 2.0, January 2004", "Apache-2.0")]
    [InlineData("GNU GENERAL PUBLIC LICENSE\nVersion 3, 29 June 2007", "GPL-3.0")]
    [InlineData("GNU LESSER GENERAL PUBLIC LICENSE\nVersion 2.1, February 1999", "LGPL-2.1")]
    [InlineData("This is free and unencumbered software released into the public domain.", "Unlicense")]
    [InlineData("All of these assets are in the public domain.", "Public-Domain")]
    public void Known_licenses_are_recognized(string text, string expected) =>
        Assert.Equal(expected, LicenseDetector.Detect(text));

    [Fact]
    public void Unknown_license_text_is_not_guessed_but_hints_are_extracted()
    {
        const string text = "Free for personal use only. Please credit the artist.";
        Assert.Null(LicenseDetector.Detect(text));
        Assert.Equal("Custom-NC-BY", LicenseDetector.CustomId(text));
        Assert.Equal("Custom", LicenseDetector.CustomId("All rights reserved."));
    }

    [Fact]
    public void Catalog_describes_conditions()
    {
        var nc = LicenseCatalog.Get("CC-BY-NC-4.0");
        Assert.Equal("CC BY-NC 4.0", nc.Name);
        Assert.Equal(Tri.No, nc.CommercialUse);
        Assert.Equal(LicenseRisk.Restricted, nc.Risk);

        Assert.Equal(LicenseRisk.Free, LicenseCatalog.Get("CC0-1.0").Risk);
        Assert.Equal(Tri.Yes, LicenseCatalog.Get("MIT").AttributionRequired);
        Assert.Equal(Tri.Yes, LicenseCatalog.Get("CC-BY-SA-4.0").ShareAlike);
        Assert.Equal(LicenseRisk.Restricted, LicenseCatalog.Get("Custom-NC").Risk);
        Assert.Equal(Tri.Yes, LicenseCatalog.Get("Custom-NC-BY").AttributionRequired);
        Assert.Equal(LicenseRisk.Unknown, LicenseCatalog.Get("Something-1.0").Risk);
    }

    // ---------- resolution: folder or above ----------

    [Fact]
    public void Nearest_folder_wins_and_overrides_beat_detection()
    {
        var docs = new[]
        {
            new FolderDocsSummary(1, "packs", "CC-BY-4.0", "packs/LICENSE", false, "packs/README.md"),
            new FolderDocsSummary(1, "packs/kenney", "CC0-1.0", "packs/kenney/License.txt", false, null),
            new FolderDocsSummary(1, "packs/other", null, null, false, "packs/other/readme.txt"),
        };
        var index = new LicenseIndex(docs, new[] { new LicenseOverride(1, "packs/other/sub", "MIT") });

        Assert.Equal("CC0-1.0", index.ResolveLicense(1, "packs/kenney/tex")!.Info.Id);          // from the folder above
        Assert.Equal("CC-BY-4.0", index.ResolveLicense(1, "packs/other")!.Info.Id);              // no license here: go up
        Assert.Equal("MIT", index.ResolveLicense(1, "packs/other/sub")!.Info.Id);                // manual override
        Assert.True(index.ResolveLicense(1, "packs/other/sub")!.IsOverride);
        Assert.Null(index.ResolveLicense(1, "elsewhere"));
        Assert.Null(index.ResolveLicense(2, "packs"));                                           // other root

        Assert.Equal("packs/other/readme.txt", index.ResolveReadme(1, "packs/other/sub")!.File);
        Assert.Equal("packs/README.md", index.ResolveReadme(1, "packs/kenney/tex")!.File);
        Assert.Equal("packs/kenney", index.ResolveLicense(1, "PACKS/KENNEY/x")!.SourceFolder, ignoreCase: true); // case-insensitive
    }

    // ---------- scanning real files ----------

    [Fact]
    public async Task License_scanner_reads_files_and_resolves_for_nested_assets()
    {
        Write("packs/kenney/LICENSE.txt", "Creative Commons Zero, CC0\nhttp://creativecommons.org/publicdomain/zero/1.0/");
        Write("packs/kenney/readme.md", "# Kenney pack\nUse these freely.");
        Write("packs/kenney/tex/rock.png", "x");
        Write("readme-only/README.txt", "Assets by Bob. License: CC BY 4.0");
        Write("readme-only/a.png", "x");
        Write("custom/LICENSE", "Free for personal use only. Credit the artist.");
        Write("custom/b.png", "x");
        Write("plain/c.png", "x");

        var root = await _repo.AddRootAsync(_lib);
        await new FolderScanner(_repo).ScanAsync(root, _lib);
        await new LicenseScanner(_repo).ScanRootAsync(root, _lib);

        var index = new LicenseIndex(await _repo.GetFolderDocSummariesAsync(), await _repo.GetLicenseOverridesAsync());

        var nested = index.ResolveLicense(root, "packs/kenney/tex");
        Assert.NotNull(nested);
        Assert.Equal("CC0-1.0", nested.Info.Id);
        Assert.Equal("packs/kenney", nested.SourceFolder);
        Assert.Equal("packs/kenney/LICENSE.txt", nested.SourceFile);
        Assert.False(nested.FromReadme);

        var readme = index.ResolveReadme(root, "packs/kenney/tex");
        Assert.Equal("packs/kenney/readme.md", readme!.File);
        var docs = await _repo.GetFolderDocsAsync(root, readme.Folder);
        Assert.Contains("Use these freely", docs!.ReadmeText);
        Assert.Contains("Creative Commons Zero", docs.LicenseText);

        var fromReadme = index.ResolveLicense(root, "readme-only")!;
        Assert.Equal("CC-BY-4.0", fromReadme.Info.Id);
        Assert.True(fromReadme.FromReadme);

        Assert.Equal("Custom-NC-BY", index.ResolveLicense(root, "custom")!.Info.Id);
        Assert.Null(index.ResolveLicense(root, "plain"));

        // overrides survive a re-scan of the license files
        await _repo.SetLicenseOverrideAsync(root, "plain", "MIT");
        await new LicenseScanner(_repo).ScanRootAsync(root, _lib);
        var again = new LicenseIndex(await _repo.GetFolderDocSummariesAsync(), await _repo.GetLicenseOverridesAsync());
        Assert.Equal("MIT", again.ResolveLicense(root, "plain")!.Info.Id);

        await _repo.SetLicenseOverrideAsync(root, "plain", null);
        Assert.Empty(await _repo.GetLicenseOverridesAsync());
    }
}
