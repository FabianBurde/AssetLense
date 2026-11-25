using System.Text.RegularExpressions;

namespace AssetManager.Core.Models;

/// <param name="Count">Assets in the bundle that are currently present on disk.</param>
public sealed record BundleInfo(long Id, string Name, int Count);

public static class BundleNames
{
    public const int MaxLength = 60;

    public static string Normalize(string name)
    {
        var s = Regex.Replace(name.Trim(), @"\s+", " ");
        return s.Length > MaxLength ? s[..MaxLength].TrimEnd() : s;
    }
}
