using AssetManager.Core.Models;

namespace AssetManager.Core.Licensing;

/// <param name="SourceFolder">The folder whose license applies (the asset's folder or one above it).</param>
/// <param name="SourceFile">Relative path of the file the license came from; null for manual overrides.</param>
/// <param name="IsOverride">Assigned by hand rather than detected.</param>
/// <param name="FromReadme">Detected in a README because the folder has no license file (less certain).</param>
public sealed record LicenseResolution(
    LicenseInfo Info, long RootId, string SourceFolder, string? SourceFile, bool IsOverride, bool FromReadme);

public sealed record ReadmeResolution(long RootId, string Folder, string File);

/// <summary>
/// In-memory lookup: which license and readme apply to a folder? The nearest folder at or above it
/// that has one wins, and a manual override beats detection in the same folder.
/// </summary>
public sealed class LicenseIndex
{
    public static readonly LicenseIndex Empty = new(Array.Empty<FolderDocsSummary>(), Array.Empty<LicenseOverride>());

    private readonly Dictionary<string, FolderDocsSummary> _docs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LicenseOverride> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public LicenseIndex(IEnumerable<FolderDocsSummary> docs, IEnumerable<LicenseOverride> overrides)
    {
        foreach (var d in docs) _docs[Key(d.RootId, d.Folder)] = d;
        foreach (var o in overrides) _overrides[Key(o.RootId, o.Folder)] = o;
    }

    public LicenseResolution? ResolveLicense(long rootId, string folder)
    {
        foreach (var f in Ancestors(folder))
        {
            var key = Key(rootId, f);
            if (_overrides.TryGetValue(key, out var o))
                return new LicenseResolution(LicenseCatalog.Get(o.LicenseId), rootId, f, null, true, false);

            if (_docs.TryGetValue(key, out var d) && d.LicenseId is not null)
                return new LicenseResolution(
                    LicenseCatalog.Get(d.LicenseId), rootId, f, d.LicenseFile, false, d.LicenseFromReadme);
        }
        return null;
    }

    public ReadmeResolution? ResolveReadme(long rootId, string folder)
    {
        foreach (var f in Ancestors(folder))
            if (_docs.TryGetValue(Key(rootId, f), out var d) && d.ReadmeFile is not null)
                return new ReadmeResolution(rootId, f, d.ReadmeFile);
        return null;
    }

    private static string Key(long rootId, string folder) => $"{rootId}|{folder}";

    /// <summary>"a/b/c" yields "a/b/c", "a/b", "a", "".</summary>
    public static IEnumerable<string> Ancestors(string folder)
    {
        var current = folder;
        while (true)
        {
            yield return current;
            if (current.Length == 0) yield break;
            var slash = current.LastIndexOf('/');
            current = slash < 0 ? "" : current[..slash];
        }
    }
}
