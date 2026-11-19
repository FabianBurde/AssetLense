using AssetManager.Core.Database;
using AssetManager.Core.Models;
using AssetManager.Core.Services;
using Microsoft.Data.Sqlite;

namespace AssetManager.Tests;

public sealed class SidebarAndCopyTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "am-tests-" + Guid.NewGuid().ToString("N"));
    private readonly AssetRepository _repo;

    public SidebarAndCopyTests()
    {
        Directory.CreateDirectory(_dir);
        var db = new AssetDatabase(Path.Combine(_dir, "test.db"));
        db.InitializeAsync().GetAwaiter().GetResult();
        _repo = new AssetRepository(db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---------- folder tree ----------

    [Fact]
    public void Tree_has_nested_nodes_sorted_by_name_with_recursive_counts()
    {
        var root = new LibraryRoot(1, @"C:\lib", "lib");
        var counts = new[]
        {
            new DirectoryCount(1, "", 2),
            new DirectoryCount(1, "tex", 3),
            new DirectoryCount(1, "tex/wood", 4),
            new DirectoryCount(1, "mesh", 1),
        };

        var node = Assert.Single(FolderTreeBuilder.Build(new[] { root }, counts, _ => true));

        Assert.Equal(10, node.Count);
        Assert.Equal(new[] { "mesh", "tex" }, node.Children.Select(c => c.Name));
        var tex = node.Children[1];
        Assert.Equal(7, tex.Count);
        Assert.Equal(3, tex.OwnCount);
        Assert.Equal("tex/wood", Assert.Single(tex.Children).Folder);
    }

    [Fact]
    public void Offline_root_marks_its_folders_offline()
    {
        var root = new LibraryRoot(1, @"Z:\gone", "gone");
        var node = Assert.Single(FolderTreeBuilder.Build(
            new[] { root }, new[] { new DirectoryCount(1, "a", 1) }, _ => false));

        Assert.True(node.IsOffline);
        Assert.True(node.Children[0].IsOffline);
    }

    // ---------- folder filtering ----------

    [Fact]
    public async Task Folder_filter_includes_subfolders_but_not_lookalike_names()
    {
        var root = await _repo.AddRootAsync(_dir);
        await _repo.UpsertManyAsync(new[]
        {
            Asset.Create(root, "tex/a.png", 1, T0),
            Asset.Create(root, "tex/wood/b.png", 1, T0),
            Asset.Create(root, "tex_old/c.png", 1, T0),
            Asset.Create(root, "mesh/m.fbx", 1, T0),
            Asset.Create(root, "top.png", 1, T0),
        });

        var tex = await _repo.SearchAsync(new AssetQuery { RootId = root, Folder = "TEX" });
        Assert.Equal(new[] { "a.png", "b.png" }, tex.Select(a => a.FileName).OrderBy(n => n));

        var wholeRoot = await _repo.SearchAsync(new AssetQuery { RootId = root, Folder = "" });
        Assert.Equal(5, wholeRoot.Count);

        var counts = await _repo.GetDirectoryCountsAsync();
        Assert.Equal(1, counts.Single(c => c.Folder == "tex/wood").Count);
        Assert.Equal(1, counts.Single(c => c.Folder == "").Count);
    }

    [Fact]
    public async Task Removing_a_root_removes_its_assets()
    {
        var root = await _repo.AddRootAsync(_dir);
        await _repo.UpsertManyAsync(new[] { Asset.Create(root, "a.png", 1, T0) });

        await _repo.RemoveRootAsync(root);

        Assert.Empty(await _repo.SearchAsync(new AssetQuery()));
        Assert.Empty(await _repo.GetRootsAsync());
    }

    // ---------- quick targets ----------

    [Fact]
    public async Task Quick_targets_can_be_added_updated_and_removed()
    {
        var added = await _repo.AddQuickTargetAsync(new QuickTarget(0, "Game", @"C:\Game", true, true, true));
        Assert.True(added.Id > 0);

        await _repo.UpdateQuickTargetAsync(added with { SortByType = false });
        var stored = Assert.Single(await _repo.GetQuickTargetsAsync());
        Assert.False(stored.SortByType);
        Assert.True(stored.SnakeCaseNames);

        await _repo.RemoveQuickTargetAsync(added.Id);
        Assert.Empty(await _repo.GetQuickTargetsAsync());
    }

    // ---------- Godot helpers ----------

    [Fact]
    public void Godot_project_is_found_from_a_subfolder()
    {
        WriteFile("MyGame/project.godot", "config_version=5");
        var sub = Path.Combine(_dir, "MyGame", "assets", "textures");
        Directory.CreateDirectory(sub);
        var projectRoot = Path.Combine(_dir, "MyGame");

        Assert.Equal(projectRoot, GodotProject.FindRoot(sub));
        Assert.Equal("MyGame/assets/textures", GodotProject.DisplayName(sub, projectRoot));
        Assert.Equal("MyGame", GodotProject.DisplayName(projectRoot, projectRoot));
        Assert.Null(GodotProject.FindRoot(_dir));
    }

    // ---------- copy planning ----------

    [Theory]
    [InlineData("Rock Wall-01.PNG", "rock_wall_01.png")]
    [InlineData("RockWall01.png", "rock_wall01.png")]
    [InlineData("  My--Tex  (final).tga", "my_tex_final.tga")]
    [InlineData("!!!.png", "file.png")]
    public void Snake_case_file_names(string input, string expected) =>
        Assert.Equal(expected, CopyPlanner.ToSnakeCaseFileName(input));

    [Fact]
    public void Plan_sorts_by_type_renames_and_detects_conflicts()
    {
        var projectDir = Path.Combine(_dir, "proj");
        var target = new QuickTarget(1, "g", projectDir, true, SortByType: true, SnakeCaseNames: true);
        var sources = new[]
        {
            new CopySource(@"D:\lib\Rock Wall.PNG", AssetType.Texture),
            new CopySource(@"D:\lib\hero.glb", AssetType.Mesh),
            new CopySource(@"D:\lib\hero.glb.import", AssetType.Other),   // Godot noise: skipped
        };

        var plan = CopyPlanner.Plan(target, sources);

        Assert.Equal(2, plan.Count);
        Assert.Equal(Path.Combine(projectDir, "textures", "rock_wall.png"), plan[0].DestinationPath);
        Assert.Equal(Path.Combine(projectDir, "models", "hero.glb"), plan[1].DestinationPath);
        Assert.All(plan, p => Assert.False(p.Exists));

        WriteFile("proj/textures/rock_wall.png", "already here");
        Assert.True(CopyPlanner.Plan(target, sources)[0].Exists);
    }

    [Fact]
    public void Plan_flags_same_file_and_collisions_inside_one_batch()
    {
        var inside = WriteFile("proj/a.png", "x");
        var target = new QuickTarget(1, "t", Path.Combine(_dir, "proj"), false, false, false);

        Assert.True(CopyPlanner.Plan(target, new[] { new CopySource(inside, AssetType.Texture) })[0].IsSameFile);

        var snake = target with { SnakeCaseNames = true };
        var plan = CopyPlanner.Plan(snake, new[]
        {
            new CopySource(@"D:\a\Rock Wall.png", AssetType.Texture),
            new CopySource(@"D:\b\rock_wall.png", AssetType.Texture),
        });
        Assert.False(plan[0].Exists);
        Assert.True(plan[1].Exists); // both would become rock_wall.png
    }

    // ---------- copy execution ----------

    private (IReadOnlyList<CopyPlanItem> Plan, string Dest) ConflictSetup()
    {
        var source = WriteFile("src/Rock.png", "new");
        var targetDir = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "Rock.png"), "old");

        var target = new QuickTarget(1, "t", targetDir, false, false, false);
        var plan = CopyPlanner.Plan(target, new[] { new CopySource(source, AssetType.Texture) });
        Assert.True(plan.Single().Exists);
        return (plan, targetDir);
    }

    [Fact]
    public async Task KeepBoth_adds_a_numbered_copy()
    {
        var (plan, dir) = ConflictSetup();

        var result = await FileCopier.ExecuteAsync(plan, ConflictPolicy.KeepBoth);

        Assert.Equal(1, result.Copied);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "Rock.png")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dir, "Rock_2.png")));
    }

    [Fact]
    public async Task Overwrite_replaces_the_existing_file()
    {
        var (plan, dir) = ConflictSetup();

        var result = await FileCopier.ExecuteAsync(plan, ConflictPolicy.Overwrite);

        Assert.Equal(1, result.Copied);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dir, "Rock.png")));
    }

    [Fact]
    public async Task Skip_leaves_the_existing_file_alone()
    {
        var (plan, dir) = ConflictSetup();

        var result = await FileCopier.ExecuteAsync(plan, ConflictPolicy.Skip);

        Assert.Equal(0, result.Copied);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "Rock.png")));
    }

    [Fact]
    public async Task Copy_creates_missing_type_folders_and_keeps_the_original()
    {
        var source = WriteFile("src/hero.glb", "mesh-bytes");
        var target = new QuickTarget(1, "g", Path.Combine(_dir, "proj"), true, true, true);
        var plan = CopyPlanner.Plan(target, new[] { new CopySource(source, AssetType.Mesh) });

        var result = await FileCopier.ExecuteAsync(plan, ConflictPolicy.KeepBoth);

        Assert.Equal(1, result.Copied);
        Assert.True(File.Exists(source));
        Assert.Equal("mesh-bytes", File.ReadAllText(Path.Combine(_dir, "proj", "models", "hero.glb")));
    }
}
