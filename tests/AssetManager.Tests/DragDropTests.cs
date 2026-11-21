using AssetManager.Core.Models;
using AssetManager.Core.Services;
using Microsoft.Data.Sqlite;

namespace AssetManager.Tests;

public sealed class DragDropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "am-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Files_dropped_onto_a_target_are_sorted_by_extension_and_copied()
    {
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        var png = Path.Combine(src, "Rock Wall.PNG");
        var glb = Path.Combine(src, "Hero.glb");
        var other = Path.Combine(src, "notes.xyz");
        foreach (var f in new[] { png, glb, other }) File.WriteAllText(f, f);

        var library = await AssetLibrary.OpenAsync(Path.Combine(_dir, "t.db"), Path.Combine(_dir, "thumbs"));
        var projectDir = Path.Combine(_dir, "proj");
        var target = new QuickTarget(1, "g", projectDir, true, SortByType: true, SnakeCaseNames: true);

        var plan = await library.PlanCopyFilesAsync(target, new[] { png, glb, other, Path.Combine(src, "missing.png") });

        Assert.Equal(3, plan.Count); // the missing file is ignored
        Assert.Equal(Path.Combine(projectDir, "textures", "rock_wall.png"), plan[0].DestinationPath);
        Assert.Equal(Path.Combine(projectDir, "models", "hero.glb"), plan[1].DestinationPath);
        Assert.Equal(Path.Combine(projectDir, "misc", "notes.xyz"), plan[2].DestinationPath);

        var result = await library.CopyAsync(plan, ConflictPolicy.KeepBoth);

        Assert.Equal(3, result.Copied);
        Assert.True(File.Exists(png)); // originals stay where they are
        Assert.True(File.Exists(Path.Combine(projectDir, "textures", "rock_wall.png")));
    }
}
