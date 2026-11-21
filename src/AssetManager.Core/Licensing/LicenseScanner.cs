using System.Text;
using AssetManager.Core.Database;
using AssetManager.Core.Models;

namespace AssetManager.Core.Licensing;

/// <summary>
/// Finds LICENSE/README-style files among the indexed files, reads them, recognizes the license,
/// and stores the result per folder. Runs after every scan.
/// </summary>
public sealed class LicenseScanner
{
    private const int MaxChars = 60_000;
    private static readonly HashSet<string> TextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { "", ".txt", ".md", ".markdown", ".rst" };

    private readonly AssetRepository _repo;

    public LicenseScanner(AssetRepository repo) => _repo = repo;

    public async Task ScanRootAsync(long rootId, string rootPath, CancellationToken ct = default)
    {
        var candidates = await _repo.GetDocumentCandidatesAsync(rootId, ct);
        var rows = new List<FolderDocs>();

        foreach (var group in candidates
                     .Where(c => TextExtensions.Contains(Path.GetExtension(c.FileName)))
                     .GroupBy(c => c.Folder, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var licenseFile = group.Where(c => LicenseRank(c.FileName) > 0)
                .OrderByDescending(c => LicenseRank(c.FileName)).FirstOrDefault();
            var readmeFile = group.Where(c => ReadmeRank(c.FileName) > 0)
                .OrderByDescending(c => ReadmeRank(c.FileName)).FirstOrDefault();
            if (licenseFile is null && readmeFile is null) continue;

            var licenseText = licenseFile is null ? null : ReadText(rootPath, licenseFile.RelativePath);
            var readmeText = readmeFile is null ? null : ReadText(rootPath, readmeFile.RelativePath);

            string? licenseId = null;
            string? licenseSource = null;
            var fromReadme = false;

            if (licenseFile is not null)
            {
                licenseSource = licenseFile.RelativePath;
                // A license file we can't identify is still worth flagging: "Custom" (+ hints).
                licenseId = licenseText is null ? null : LicenseDetector.Detect(licenseText) ?? LicenseDetector.CustomId(licenseText);
            }
            else if (readmeText is not null)
            {
                // No license file: fall back to what the README says (less certain, marked as such).
                licenseId = LicenseDetector.Detect(readmeText);
                if (licenseId is not null)
                {
                    licenseSource = readmeFile!.RelativePath;
                    fromReadme = true;
                }
            }

            rows.Add(new FolderDocs(
                rootId, group.Key, licenseId, licenseSource,
                fromReadme ? null : licenseText, fromReadme,
                readmeFile?.RelativePath, readmeText));
        }

        await _repo.ReplaceFolderDocsAsync(rootId, rows, ct);
    }

    private static int LicenseRank(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (n.StartsWith("license") || n.StartsWith("licence")) return 4;
        if (n.StartsWith("copying")) return 3;
        if (n.StartsWith("unlicense")) return 2;
        if (n.StartsWith("cc0")) return 1;
        return 0;
    }

    private static int ReadmeRank(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        if (n.StartsWith("readme")) return 4;
        if (n.StartsWith("credits") || n.StartsWith("attribution")) return 3;
        if (n.StartsWith("notice")) return 2;
        return 0;
    }

    private static string? ReadText(string rootPath, string relativePath)
    {
        var full = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            using var reader = new StreamReader(full, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxChars + 1];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var text = new string(buffer, 0, Math.Min(read, MaxChars));
            return read > MaxChars ? text + "\n\n[… text truncated]" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
