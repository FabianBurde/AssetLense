using System.Text.RegularExpressions;

namespace AssetManager.Core.Licensing;

/// <summary>
/// Recognizes well-known licenses from their text by looking for telltale phrases and URLs.
/// This is a heuristic: it can be wrong, which is why the UI lets you override the result.
/// </summary>
public static class LicenseDetector
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex CcUrl = new(
        @"creativecommons\.org/licenses/(by(?:-nc)?(?:-sa|-nd)?)/(\d\.\d)", Opts);

    private static readonly Regex CcName = new(
        @"creative commons attribution(?:[ -]non-?commercial)?(?:[ -](?:share-?alike|no-?derivs?|no-?derivatives?))?(?: (\d\.\d))?", Opts);

    private static readonly Regex CcAbbreviation = new(
        @"\bcc[ -]by(?:[ -](nc))?(?:[ -](sa|nd))?(?:[ -]v?(\d\.\d))?\b", Opts);

    private static readonly Regex Cc0 = new(
        @"\bcc0\b|publicdomain/zero|creative commons zero", Opts);

    private static readonly Regex NonCommercial = new(
        @"non-?commercial|non commercial|not for commercial|no commercial|personal use only|commercial use is not|not.{0,20}commercially|forbidden.{0,20}commercial", Opts);

    private static readonly Regex Attribution = new(@"attribution|credit|acknowledg", Opts);

    /// <summary>A license id such as "CC-BY-4.0", "MIT" or "GPL-3.0", or null if nothing was recognized.</summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Normalize(text);

        // Creative Commons first: the BY variants are the most specific, then CC0.
        var m = CcUrl.Match(t);
        if (m.Success)
            return Cc(m.Groups[1].Value.Contains("nc"), SaOrNd(m.Groups[1].Value), m.Groups[2].Value);

        m = CcName.Match(t);
        if (m.Success)
        {
            var v = m.Value;
            var nc = v.Contains("commercial");
            string? sd = v.Contains("share") ? "SA" : Regex.IsMatch(v, "no-?deriv") ? "ND" : null;
            return Cc(nc, sd, m.Groups[1].Success ? m.Groups[1].Value : null);
        }

        m = CcAbbreviation.Match(t);
        if (m.Success)
            return Cc(m.Groups[1].Success,
                m.Groups[2].Success ? m.Groups[2].Value.ToUpperInvariant() : null,
                m.Groups[3].Success ? m.Groups[3].Value : null);

        if (Cc0.IsMatch(t)) return "CC0-1.0";

        if (t.Contains("this is free and unencumbered software released into the public domain")) return "Unlicense";
        if (t.Contains("sil open font license")) return "OFL-1.1";

        if (t.Contains("gnu affero general public license")) return "AGPL-3.0";
        if (t.Contains("gnu lesser general public license"))
            return t.Contains("version 3") ? "LGPL-3.0" : t.Contains("version 2.1") ? "LGPL-2.1" : "LGPL";
        if (t.Contains("gnu general public license"))
            return t.Contains("version 3") ? "GPL-3.0" : t.Contains("version 2") ? "GPL-2.0" : "GPL";

        if (t.Contains("apache license") && (t.Contains("version 2.0") || t.Contains("version 2"))) return "Apache-2.0";

        if (t.Contains("permission is hereby granted, free of charge, to any person obtaining a copy")) return "MIT";
        if (t.Contains("permission to use, copy, modify, and/or distribute this software for any purpose")) return "ISC";

        if (t.Contains("redistribution and use in source and binary forms"))
            return t.Contains("neither the name") || t.Contains("names of its contributors")
                ? "BSD-3-Clause" : "BSD-2-Clause";

        if (t.Contains("public domain")) return "Public-Domain";

        return null;
    }

    /// <summary>
    /// For a license file we can't identify: "Custom", plus "-NC" if it mentions non-commercial terms
    /// and "-BY" if it mentions credit or attribution.
    /// </summary>
    public static string CustomId(string? text)
    {
        var t = Normalize(text ?? "");
        return "Custom" + (NonCommercial.IsMatch(t) ? "-NC" : "") + (Attribution.IsMatch(t) ? "-BY" : "");
    }

    private static string Cc(bool nc, string? saOrNd, string? version) =>
        "CC-BY" + (nc ? "-NC" : "") + (saOrNd is null ? "" : "-" + saOrNd) + (version is null ? "" : "-" + version);

    private static string? SaOrNd(string by) =>
        by.EndsWith("-sa", StringComparison.Ordinal) ? "SA" : by.EndsWith("-nd", StringComparison.Ordinal) ? "ND" : null;

    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"\s+", " ");
}
