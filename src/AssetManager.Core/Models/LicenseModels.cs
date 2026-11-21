namespace AssetManager.Core.Models;

/// <summary>A license/readme file found in a folder, as indexed by the scanner.</summary>
public sealed record DocCandidate(string Folder, string RelativePath, string FileName);

/// <summary>License and readme information detected for one folder (stored with the file texts).</summary>
public sealed record FolderDocs(
    long RootId, string Folder,
    string? LicenseId, string? LicenseFile, string? LicenseText, bool LicenseFromReadme,
    string? ReadmeFile, string? ReadmeText);

/// <summary>The same without the (large) texts, for the in-memory lookup used by the grid.</summary>
public sealed record FolderDocsSummary(
    long RootId, string Folder, string? LicenseId, string? LicenseFile, bool LicenseFromReadme, string? ReadmeFile);

/// <summary>A license the user assigned manually to a folder (and everything below it).</summary>
public sealed record LicenseOverride(long RootId, string Folder, string LicenseId);
