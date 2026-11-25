using System.Globalization;
using System.IO.Compression;
using System.Text;
using AssetManager.Core.Licensing;
using AssetManager.Core.Models;
using AssetManager.Core.Services;

namespace AssetManager.Core.Bundling;

public enum ZipLayout { KeepFolders, Flat, ByType }

public sealed record BundleExportOptions(
    ZipLayout Layout = ZipLayout.KeepFolders,
    bool SnakeCaseNames = false,
    bool IncludeLicenseFiles = true,
    bool IncludeCredits = true);

/// <summary>An asset together with everything the exporter needs to know about where it came from.</summary>
public sealed record ExportItem(
    Asset Asset, string RootName, string RootPath, string FullPath,
    LicenseResolution? License, ReadmeResolution? Readme);

public sealed record ZipEntryPlan(string SourcePath, string EntryName);

public sealed record ExportProgress(int Done, int Total);

public sealed record BundleExportResult(string Path, int Files, long ArchiveBytes, IReadOnlyList<string> Skipped);

public sealed record LicenseIssue(string Category, int Count, IReadOnlyList<string> Examples);

public sealed record LicenseReport(IReadOnlyList<LicenseIssue> Issues)
{
    public bool HasIssues => Issues.Count > 0;
}

/// <summary>Finds assets that may not be safe to hand to someone else (no, restricted, or unrecognized license).</summary>
public static class BundleLicenseCheck
{
    public static LicenseReport Analyze(IEnumerable<ExportItem> items)
    {
        var none = new List<string>();
        var restricted = new List<string>();
        var unknown = new List<string>();

        foreach (var item in items)
        {
            if (item.Asset.IsOwn) continue; // your own work needs no third-party license

            if (item.License is null) none.Add(item.Asset.FileName);
            else if (item.License.Info.Risk == LicenseRisk.Restricted) restricted.Add(item.Asset.FileName);
            else if (item.License.Info.Risk == LicenseRisk.Unknown) unknown.Add(item.Asset.FileName);
        }

        var issues = new List<LicenseIssue>();
        Add(issues, "no license", none);
        Add(issues, "a restricted license (non-commercial or no derivatives)", restricted);
        Add(issues, "an unrecognized license", unknown);
        return new LicenseReport(issues);

        static void Add(List<LicenseIssue> list, string category, List<string> names)
        {
            if (names.Count > 0) list.Add(new LicenseIssue(category, names.Count, names.Take(3).ToList()));
        }
    }
}

/// <summary>Decides the path of every file inside the zip.</summary>
public static class ZipPlanner
{
    public const string CreditsName = "CREDITS.txt";

    public static IReadOnlyList<ZipEntryPlan> PlanFiles(IReadOnlyList<ExportItem> items, BundleExportOptions options)
    {
        var multipleRoots = items.Select(i => i.Asset.RootId).Distinct().Count() > 1;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { CreditsName };
        var plan = new List<ZipEntryPlan>();

        foreach (var item in items)
        {
            var name = item.Asset.FileName;
            if (options.SnakeCaseNames)
                name = CopyPlanner.ToSnakeCaseFileName(name);

            var entry = options.Layout switch
            {
                ZipLayout.Flat => name,
                ZipLayout.ByType => $"{CopyPlanner.TypeFolderName(item.Asset.Type)}/{name}",
                _ => KeepFoldersEntry(item, name, multipleRoots),
            };

            plan.Add(new ZipEntryPlan(item.FullPath, Unique(entry, used)));
        }

        return plan;
    }

    /// <summary>The license and readme files of the folders the assets came from, under licenses/.</summary>
    public static IReadOnlyList<ZipEntryPlan> PlanLicenseFiles(
        IReadOnlyList<ExportItem> items, IEnumerable<string> takenNames)
    {
        var used = new HashSet<string>(takenNames, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<ZipEntryPlan>();

        void Add(ExportItem item, string folder, string relativeFile)
        {
            var full = FullPathOf(item.RootPath, relativeFile);
            if (!seen.Add(full)) return;

            var key = folder.Length == 0 ? SafeSegment(item.RootName) : SafeSegment(folder.Replace('/', '_'));
            var entry = $"licenses/{key}/{Path.GetFileName(relativeFile)}";
            plan.Add(new ZipEntryPlan(full, Unique(entry, used)));
        }

        foreach (var item in items)
        {
            if (item.Asset.IsOwn) continue;

            if (item.License is { IsOverride: false, SourceFile: { } licenseFile })
                Add(item, item.License.SourceFolder, licenseFile);
            if (item.Readme is { } readme)
                Add(item, readme.Folder, readme.File);
        }

        return plan;
    }

    internal static string FullPathOf(string rootPath, string relativeFile) =>
        Path.Combine(rootPath, relativeFile.Replace('/', Path.DirectorySeparatorChar));

    private static string KeepFoldersEntry(ExportItem item, string name, bool multipleRoots)
    {
        var prefix = multipleRoots ? SafeSegment(item.RootName) + "/" : "";
        var folder = item.Asset.Folder;
        return prefix + (folder.Length == 0 ? name : $"{folder}/{name}");
    }

    private static string SafeSegment(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = text.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "root" : result;
    }

    private static string Unique(string entry, HashSet<string> used)
    {
        if (used.Add(entry)) return entry;

        var slash = entry.LastIndexOf('/');
        var dir = slash < 0 ? "" : entry[..(slash + 1)];
        var file = entry[(slash + 1)..];
        var stem = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);

        for (var n = 2; ; n++)
        {
            var candidate = $"{dir}{stem}_{n}{ext}";
            if (used.Add(candidate)) return candidate;
        }
    }
}

public static class BundleExporter
{
    private static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".mp3", ".ogg", ".flac", ".zip", ".7z", ".rar", ".mp4",
    };

    /// <summary>
    /// Writes the zip to a temporary file first and moves it into place when it is complete, so a
    /// cancelled or failed export never leaves a half-written archive behind.
    /// </summary>
    public static Task<BundleExportResult> ExportAsync(
        IReadOnlyList<ExportItem> items, string destinationPath, BundleExportOptions options,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Export(items, destinationPath, options, progress, ct), ct);

    private static BundleExportResult Export(
        IReadOnlyList<ExportItem> items, string destinationPath, BundleExportOptions options,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var filePlan = ZipPlanner.PlanFiles(items, options);
        var licensePlan = options.IncludeLicenseFiles
            ? ZipPlanner.PlanLicenseFiles(items, filePlan.Select(p => p.EntryName))
            : Array.Empty<ZipEntryPlan>();

        var total = filePlan.Count + licensePlan.Count + (options.IncludeCredits ? 1 : 0);
        var done = 0;
        var files = 0;
        var skipped = new List<string>();
        var added = new List<(ExportItem Item, string Entry)>();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var temp = destinationPath + ".tmp";

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                for (var i = 0; i < filePlan.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (AddFile(archive, filePlan[i]))
                    {
                        files++;
                        added.Add((items[i], filePlan[i].EntryName));
                    }
                    else
                    {
                        skipped.Add(filePlan[i].SourcePath);
                    }
                    progress?.Report(new ExportProgress(++done, total));
                }

                var licenseEntries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var plan in licensePlan)
                {
                    ct.ThrowIfCancellationRequested();
                    if (AddFile(archive, plan))
                        licenseEntries[plan.SourcePath] = plan.EntryName;
                    progress?.Report(new ExportProgress(++done, total));
                }

                if (options.IncludeCredits)
                {
                    ct.ThrowIfCancellationRequested();
                    AddText(archive, ZipPlanner.CreditsName, BuildCredits(added, licenseEntries, DateTime.UtcNow));
                    progress?.Report(new ExportProgress(++done, total));
                }
            }

            File.Move(temp, destinationPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return new BundleExportResult(destinationPath, files, new FileInfo(destinationPath).Length, skipped);
    }

    private static bool AddFile(ZipArchive archive, ZipEntryPlan plan)
    {
        try
        {
            // Open the source first, so a missing file never leaves an empty entry in the zip.
            using var source = new FileStream(
                plan.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var level = AlreadyCompressed.Contains(Path.GetExtension(plan.SourcePath))
                ? CompressionLevel.NoCompression
                : CompressionLevel.Optimal;

            var entry = archive.CreateEntry(plan.EntryName, level);
            entry.LastWriteTime = ClampZipTime(File.GetLastWriteTime(plan.SourcePath));

            using var target = entry.Open();
            source.CopyTo(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void AddText(ZipArchive archive, string entryName, string text)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string BuildCredits(
        IReadOnlyList<(ExportItem Item, string Entry)> added,
        IReadOnlyDictionary<string, string> licenseEntries, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Credits and licenses");
        sb.AppendLine($"Generated by Asset Manager on {nowUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC");
        sb.AppendLine();

        foreach (var (item, entry) in added)
        {
            string license;
            string source;

            if (item.Asset.IsOwn)
            {
                license = "Own work";
                source = "-";
            }
            else if (item.License is null)
            {
                license = "No license found";
                source = "-";
            }
            else
            {
                license = item.License.Info.Name;
                if (item.License.IsOverride)
                {
                    source = "Assigned manually in Asset Manager";
                }
                else
                {
                    var full = item.License.SourceFile is null ? null : ZipPlanner.FullPathOf(item.RootPath, item.License.SourceFile);
                    source = full is not null && licenseEntries.TryGetValue(full, out var inZip)
                        ? inZip
                        : item.License.SourceFile ?? "-";
                    if (item.License.FromReadme) source += " (detected in a README)";
                }
            }

            sb.AppendLine(entry);
            sb.AppendLine($"  License: {license}");
            sb.AppendLine($"  Source: {source}");
            sb.AppendLine();
        }

        sb.AppendLine("License information is detected automatically and can be wrong.");
        sb.AppendLine("Check the original license texts before you redistribute these assets.");
        return sb.ToString();
    }

    // Zip stores times from 1980 on.
    private static DateTimeOffset ClampZipTime(DateTime local)
    {
        var time = new DateTimeOffset(local);
        var min = new DateTimeOffset(1980, 1, 1, 0, 0, 0, time.Offset);
        return time < min ? min : time;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
