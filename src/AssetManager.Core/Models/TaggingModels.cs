using System.Text.RegularExpressions;

namespace AssetManager.Core.Models;

public sealed record TagInfo(long Id, string Name, int Count);

/// <summary>How many of the selected assets carry a tag.</summary>
public sealed record TagCount(string Name, int Count);

public sealed record LibraryCounts(int Favorites, int Own, int Tagged, int Bundled);

public static class TagNames
{
    public const int MaxLength = 40;

    public static string Normalize(string name)
    {
        var s = Regex.Replace(name.Trim(), @"\s+", " ");
        return s.Length > MaxLength ? s[..MaxLength].TrimEnd() : s;
    }

    /// <summary>Splits "wood, tileable; rough" into normalized, distinct tag names.</summary>
    public static IReadOnlyList<string> Split(string? input) =>
        (input ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
