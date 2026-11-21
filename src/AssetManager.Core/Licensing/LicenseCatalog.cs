using System.Text.RegularExpressions;

namespace AssetManager.Core.Licensing;

public enum Tri { Unknown, Yes, No }

public enum LicenseRisk { Free, Conditions, Restricted, Unknown }

/// <summary>What a license allows, in plain terms. A summary aid, not legal advice.</summary>
public sealed record LicenseInfo(
    string Id, string Name, string ShortName,
    Tri CommercialUse, Tri Modification, Tri AttributionRequired, Tri ShareAlike,
    LicenseRisk Risk, string Summary);

public static class LicenseCatalog
{
    private sealed record Entry(
        string Display, string Short, Tri Commercial, Tri Modification, Tri Attribution, Tri ShareAlike,
        LicenseRisk Risk, string Summary);

    private const Tri Y = Tri.Yes;
    private const Tri N = Tri.No;

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CC0"] = new("CC0", "CC0", Y, Y, N, N, LicenseRisk.Free,
            "No restrictions. Credit is not required."),
        ["CC-BY"] = new("CC BY", "BY", Y, Y, Y, N, LicenseRisk.Conditions,
            "Free to use, including commercially, if you credit the author and note any changes."),
        ["CC-BY-SA"] = new("CC BY-SA", "BY-SA", Y, Y, Y, Y, LicenseRisk.Conditions,
            "Credit the author. Adaptations must be shared under the same license."),
        ["CC-BY-NC"] = new("CC BY-NC", "BY-NC", N, Y, Y, N, LicenseRisk.Restricted,
            "Credit the author. Commercial use is not allowed."),
        ["CC-BY-NC-SA"] = new("CC BY-NC-SA", "BY-NC-SA", N, Y, Y, Y, LicenseRisk.Restricted,
            "Credit the author. No commercial use; adaptations must use the same license."),
        ["CC-BY-ND"] = new("CC BY-ND", "BY-ND", Y, N, Y, N, LicenseRisk.Restricted,
            "Credit the author. No derivatives: you may not modify the asset (resizing or editing may count)."),
        ["CC-BY-NC-ND"] = new("CC BY-NC-ND", "BY-NC-ND", N, N, Y, N, LicenseRisk.Restricted,
            "Credit the author. No commercial use and no modifications."),
        ["MIT"] = new("MIT", "MIT", Y, Y, Y, N, LicenseRisk.Conditions,
            "Free to use and modify; keep the copyright and license notice."),
        ["ISC"] = new("ISC", "ISC", Y, Y, Y, N, LicenseRisk.Conditions,
            "Free to use and modify; keep the copyright and license notice."),
        ["BSD-2-Clause"] = new("BSD 2-Clause", "BSD", Y, Y, Y, N, LicenseRisk.Conditions,
            "Free to use and modify; keep the copyright notice and license text."),
        ["BSD-3-Clause"] = new("BSD 3-Clause", "BSD", Y, Y, Y, N, LicenseRisk.Conditions,
            "Free to use and modify; keep the notice, and don’t use the author’s name to endorse your work."),
        ["Apache"] = new("Apache", "Apache", Y, Y, Y, N, LicenseRisk.Conditions,
            "Free to use and modify; keep the notices and state significant changes."),
        ["GPL"] = new("GPL", "GPL", Y, Y, Y, Y, LicenseRisk.Conditions,
            "Strong copyleft: derivative works must use the same license. Using it in a game may require releasing the game under the GPL."),
        ["LGPL"] = new("LGPL", "LGPL", Y, Y, Y, Y, LicenseRisk.Conditions,
            "Weak copyleft: changes to the work itself must be shared under the same license."),
        ["AGPL"] = new("AGPL", "AGPL", Y, Y, Y, Y, LicenseRisk.Conditions,
            "Strong copyleft, including for network use. Read it carefully before using it in a product."),
        ["Unlicense"] = new("Unlicense", "PD", Y, Y, N, N, LicenseRisk.Free,
            "Released into the public domain. No restrictions."),
        ["Public-Domain"] = new("Public domain", "PD", Y, Y, N, N, LicenseRisk.Free,
            "Declared public domain. No restrictions, but check the wording of the notice."),
        ["OFL"] = new("SIL OFL", "OFL", Y, Y, Y, Y, LicenseRisk.Conditions,
            "Font license: free to use and embed; the font itself may not be sold on its own, and derivatives stay under the OFL."),
    };

    /// <summary>The licenses offered when assigning one by hand.</summary>
    public static IReadOnlyList<string> CommonIds { get; } = new[]
    {
        "CC0-1.0", "CC-BY-4.0", "CC-BY-SA-4.0", "CC-BY-NC-4.0", "CC-BY-NC-SA-4.0", "CC-BY-ND-4.0",
        "CC-BY-NC-ND-4.0", "MIT", "Apache-2.0", "BSD-3-Clause", "BSD-2-Clause", "ISC", "GPL-3.0",
        "LGPL-3.0", "AGPL-3.0", "Unlicense", "OFL-1.1", "Public-Domain", "Custom",
    };

    /// <summary>
    /// Looks up a license by id such as "CC-BY-4.0". Ids starting with "Custom" are licenses we
    /// couldn't identify ("Custom-NC": it mentions non-commercial terms, "Custom-BY": it mentions credit).
    /// </summary>
    public static LicenseInfo Get(string id)
    {
        if (id.StartsWith("Custom", StringComparison.OrdinalIgnoreCase))
            return Custom(id);

        var (key, version) = SplitVersion(id);
        if (!Entries.TryGetValue(key, out var e))
            return new LicenseInfo(id, id, "?", Tri.Unknown, Tri.Unknown, Tri.Unknown, Tri.Unknown,
                LicenseRisk.Unknown, "Unrecognized license. Read the license text before using this asset.");

        var name = version is null ? e.Display : $"{e.Display} {version}";
        return new LicenseInfo(id, name, e.Short, e.Commercial, e.Modification, e.Attribution, e.ShareAlike, e.Risk, e.Summary);
    }

    private static LicenseInfo Custom(string id)
    {
        var mentionsNonCommercial = id.Contains("-NC", StringComparison.OrdinalIgnoreCase);
        var mentionsCredit = id.Contains("-BY", StringComparison.OrdinalIgnoreCase);

        var summary = "A custom license that wasn’t recognized automatically. Read the license text before using this asset.";
        if (mentionsNonCommercial)
            summary = "A custom license that mentions non-commercial terms. Read it carefully before any commercial use.";
        else if (mentionsCredit)
            summary = "A custom license that mentions credit or attribution. Read it to see what is required.";

        return new LicenseInfo(
            id, "Custom license", "Custom", Tri.Unknown, Tri.Unknown,
            mentionsCredit ? Tri.Yes : Tri.Unknown, Tri.Unknown,
            mentionsNonCommercial ? LicenseRisk.Restricted : LicenseRisk.Unknown, summary);
    }

    private static (string Key, string? Version) SplitVersion(string id)
    {
        var m = Regex.Match(id, @"^(.+)-(\d+(?:\.\d+)?)$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (id, null);
    }
}
