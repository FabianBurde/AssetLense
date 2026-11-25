using System.Text;
using AssetManager.Core.Models;
using Microsoft.Data.Sqlite;

namespace AssetManager.Core.Database;

public sealed class AssetRepository
{
    private const string Columns =
        "id, root_id, relative_path, file_name, extension, asset_type, size_bytes, " +
        "content_hash, width, height, modified_utc, added_utc, is_missing, directory, is_favorite, is_own";

    private readonly AssetDatabase _db;

    public AssetRepository(AssetDatabase db) => _db = db;

    /// <summary>Registers a library folder (or returns the existing id if already registered).</summary>
    public async Task<long> AddRootAsync(string path, string? displayName = null, CancellationToken ct = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var name = displayName ?? Path.GetFileName(full);
        if (string.IsNullOrEmpty(name)) name = full; // drive roots like "D:\"

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO library_roots (path, display_name) VALUES ($path, $name)
            ON CONFLICT(path) DO UPDATE SET display_name = excluded.display_name
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$path", full);
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Inserts new files and refreshes existing ones in a single transaction (fast enough for 100k+ rows).
    /// If a file's size or modified time changed, its hash and dimensions are cleared so they get recomputed.
    /// </summary>
    public async Task<int> UpsertManyAsync(IEnumerable<Asset> assets, long scanToken = 0, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO assets
                (root_id, relative_path, file_name, extension, asset_type, size_bytes, modified_utc, added_utc, last_seen, directory)
            VALUES
                ($root, $path, $name, $ext, $type, $size, $mod, $added, $seen, $dir)
            ON CONFLICT(root_id, relative_path) DO UPDATE SET
                directory    = excluded.directory,
                file_name    = excluded.file_name,
                extension    = excluded.extension,
                asset_type   = excluded.asset_type,
                content_hash = CASE WHEN assets.size_bytes = excluded.size_bytes
                                     AND assets.modified_utc = excluded.modified_utc
                                    THEN assets.content_hash ELSE NULL END,
                width        = CASE WHEN assets.size_bytes = excluded.size_bytes
                                     AND assets.modified_utc = excluded.modified_utc
                                    THEN assets.width ELSE NULL END,
                height       = CASE WHEN assets.size_bytes = excluded.size_bytes
                                     AND assets.modified_utc = excluded.modified_utc
                                    THEN assets.height ELSE NULL END,
                size_bytes   = excluded.size_bytes,
                modified_utc = excluded.modified_utc,
                is_missing   = 0,
                last_seen    = excluded.last_seen;
            """;

        var pRoot = cmd.Parameters.Add("$root", SqliteType.Integer);
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pExt = cmd.Parameters.Add("$ext", SqliteType.Text);
        var pType = cmd.Parameters.Add("$type", SqliteType.Integer);
        var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
        var pMod = cmd.Parameters.Add("$mod", SqliteType.Integer);
        var pAdded = cmd.Parameters.Add("$added", SqliteType.Integer);
        cmd.Parameters.Add("$seen", SqliteType.Integer).Value = scanToken;
        var pDir = cmd.Parameters.Add("$dir", SqliteType.Text);

        var count = 0;
        foreach (var a in assets)
        {
            ct.ThrowIfCancellationRequested();
            pRoot.Value = a.RootId;
            pPath.Value = a.RelativePath;
            pName.Value = a.FileName;
            pExt.Value = a.Extension;
            pType.Value = (int)a.Type;
            pSize.Value = a.SizeBytes;
            pMod.Value = a.ModifiedUtc.ToUnixTimeMilliseconds();
            pAdded.Value = a.AddedUtc.ToUnixTimeMilliseconds();
            pDir.Value = a.Folder;
            await cmd.ExecuteNonQueryAsync(ct);
            count++;
        }

        await tx.CommitAsync(ct);
        return count;
    }

    /// <summary>Flags files of this root that the scan with the given token did not see.</summary>
    public async Task<int> MarkMissingAsync(long rootId, long scanToken, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE assets SET is_missing = 1 WHERE root_id = $root AND last_seen < $token AND is_missing = 0;";
        cmd.Parameters.AddWithValue("$root", rootId);
        cmd.Parameters.AddWithValue("$token", scanToken);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountPendingHashesAsync(long? rootId = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM assets
            WHERE content_hash IS NULL AND is_missing = 0 AND ($root IS NULL OR root_id = $root);
            """;
        cmd.Parameters.AddWithValue("$root", (object?)rootId ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>Next batch of un-hashed files with id greater than <paramref name="afterId"/> (keyset paging).</summary>
    public async Task<IReadOnlyList<HashCandidate>> GetPendingHashesAsync(
        long afterId, int limit, long? rootId = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, r.path, a.relative_path, a.size_bytes
            FROM assets a JOIN library_roots r ON r.id = a.root_id
            WHERE a.id > $after AND a.content_hash IS NULL AND a.is_missing = 0
              AND ($root IS NULL OR a.root_id = $root)
            ORDER BY a.id LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$after", afterId);
        cmd.Parameters.AddWithValue("$root", (object?)rootId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<HashCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new HashCandidate(r.GetInt64(0), ResolvePath(r.GetString(1), r.GetString(2)), r.GetInt64(3)));
        }
        return list;
    }

    public async Task SetHashesAsync(IEnumerable<(long Id, string Hash)> hashes, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE assets SET content_hash = $hash WHERE id = $id;";
        var pHash = cmd.Parameters.Add("$hash", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);

        foreach (var (id, hash) in hashes)
        {
            pId.Value = id;
            pHash.Value = hash;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Textures that are hashed but have no dimensions yet (dimensions are filled in by the thumbnail pass).</summary>
    public async Task<int> CountPendingThumbnailsAsync(long? rootId = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM assets
            WHERE asset_type = $tex AND content_hash IS NOT NULL AND width IS NULL AND is_missing = 0
              AND ($root IS NULL OR root_id = $root);
            """;
        cmd.Parameters.AddWithValue("$tex", (int)AssetType.Texture);
        cmd.Parameters.AddWithValue("$root", (object?)rootId ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<ThumbnailCandidate>> GetPendingThumbnailsAsync(
        long afterId, int limit, long? rootId = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, r.path, a.relative_path, a.content_hash
            FROM assets a JOIN library_roots r ON r.id = a.root_id
            WHERE a.id > $after AND a.asset_type = $tex AND a.content_hash IS NOT NULL
              AND a.width IS NULL AND a.is_missing = 0 AND ($root IS NULL OR a.root_id = $root)
            ORDER BY a.id LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$after", afterId);
        cmd.Parameters.AddWithValue("$tex", (int)AssetType.Texture);
        cmd.Parameters.AddWithValue("$root", (object?)rootId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<ThumbnailCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ThumbnailCandidate(r.GetInt64(0), ResolvePath(r.GetString(1), r.GetString(2)), r.GetString(3)));
        return list;
    }

    /// <summary>Stores image dimensions. 0x0 is a sentinel meaning "could not be decoded".</summary>
    public async Task SetDimensionsAsync(IEnumerable<(long Id, int Width, int Height)> dimensions, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE assets SET width = $w, height = $h WHERE id = $id;";
        var pW = cmd.Parameters.Add("$w", SqliteType.Integer);
        var pH = cmd.Parameters.Add("$h", SqliteType.Integer);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);

        foreach (var (id, w, h) in dimensions)
        {
            pId.Value = id;
            pW.Value = w;
            pH.Value = h;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Forgets all dimensions so the thumbnail pass regenerates everything (e.g. after wiping the cache).</summary>
    public async Task<int> ResetThumbnailsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE assets SET width = NULL, height = NULL WHERE width IS NOT NULL;";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<LibraryRoot>> GetRootsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, display_name FROM library_roots ORDER BY display_name COLLATE NOCASE;";

        var list = new List<LibraryRoot>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LibraryRoot(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>File counts per (root, folder), for building the sidebar tree.</summary>
    public async Task<IReadOnlyList<DirectoryCount>> GetDirectoryCountsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT root_id, directory, COUNT(*) FROM assets
            WHERE is_missing = 0 GROUP BY root_id, directory;
            """;

        var list = new List<DirectoryCount>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DirectoryCount(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        return list;
    }

    /// <summary>Removes a root and (via cascade) its index entries and tags. Files on disk are never touched.</summary>
    public async Task RemoveRootAsync(long rootId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM library_roots WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", rootId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<QuickTarget>> GetQuickTargetsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, path, is_godot, sort_by_type, snake_case
            FROM quick_targets ORDER BY name COLLATE NOCASE;
            """;

        var list = new List<QuickTarget>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new QuickTarget(r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.GetInt32(3) != 0, r.GetInt32(4) != 0, r.GetInt32(5) != 0));
        return list;
    }

    public async Task<QuickTarget> AddQuickTargetAsync(QuickTarget target, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO quick_targets (name, path, is_godot, sort_by_type, snake_case)
            VALUES ($name, $path, $godot, $sort, $snake)
            ON CONFLICT(path) DO UPDATE SET
                name = excluded.name, is_godot = excluded.is_godot,
                sort_by_type = excluded.sort_by_type, snake_case = excluded.snake_case
            RETURNING id;
            """;
        AddTargetParameters(cmd, target);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return target with { Id = id };
    }

    public async Task UpdateQuickTargetAsync(QuickTarget target, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE quick_targets
            SET name = $name, path = $path, is_godot = $godot, sort_by_type = $sort, snake_case = $snake
            WHERE id = $id;
            """;
        AddTargetParameters(cmd, target);
        cmd.Parameters.AddWithValue("$id", target.Id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveQuickTargetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM quick_targets WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddTargetParameters(SqliteCommand cmd, QuickTarget t)
    {
        cmd.Parameters.AddWithValue("$name", t.Name);
        cmd.Parameters.AddWithValue("$path", t.Path);
        cmd.Parameters.AddWithValue("$godot", t.IsGodot ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort", t.SortByType ? 1 : 0);
        cmd.Parameters.AddWithValue("$snake", t.SnakeCaseNames ? 1 : 0);
    }

    // ------------------------------------------------------------ favorites / own

    public Task SetFavoriteAsync(IReadOnlyList<long> ids, bool value, CancellationToken ct = default) =>
        SetFlagAsync("is_favorite", ids, value, ct);

    public Task SetOwnAsync(IReadOnlyList<long> ids, bool value, CancellationToken ct = default) =>
        SetFlagAsync("is_own", ids, value, ct);

    private async Task SetFlagAsync(string column, IReadOnlyList<long> ids, bool value, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE assets SET {column} = $v WHERE id = $id;"; // column is an internal constant
        cmd.Parameters.AddWithValue("$v", value ? 1 : 0);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);

        foreach (var id in ids)
        {
            pId.Value = id;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Marks (or unmarks) every present file in a folder and its subfolders as the user's own work.</summary>
    public async Task<int> SetOwnForFolderAsync(long rootId, string folder, bool value, CancellationToken ct = default)
    {
        var f = folder.Trim('/');

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = f.Length == 0
            ? "UPDATE assets SET is_own = $v WHERE root_id = $root AND is_missing = 0;"
            : "UPDATE assets SET is_own = $v WHERE root_id = $root AND is_missing = 0 " +
              "AND (directory = $folder OR directory LIKE $like ESCAPE '\\');";
        cmd.Parameters.AddWithValue("$v", value ? 1 : 0);
        cmd.Parameters.AddWithValue("$root", rootId);
        if (f.Length > 0)
        {
            cmd.Parameters.AddWithValue("$folder", f);
            cmd.Parameters.AddWithValue("$like", EscapeLike(f) + "/%");
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<LibraryCounts> GetFilterCountsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM assets WHERE is_favorite = 1 AND is_missing = 0),
              (SELECT COUNT(*) FROM assets WHERE is_own = 1 AND is_missing = 0),
              (SELECT COUNT(DISTINCT xt.asset_id) FROM asset_tags xt
                 JOIN assets a ON a.id = xt.asset_id WHERE a.is_missing = 0),
              (SELECT COUNT(DISTINCT bx.asset_id) FROM bundle_assets bx
                 JOIN assets b ON b.id = bx.asset_id WHERE b.is_missing = 0);
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new LibraryCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
    }

    // ------------------------------------------------------------ tags

    public async Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.name, COUNT(a.id)
            FROM tags t
            LEFT JOIN asset_tags atg ON atg.tag_id = t.id
            LEFT JOIN assets a ON a.id = atg.asset_id AND a.is_missing = 0
            GROUP BY t.id, t.name
            ORDER BY t.name COLLATE NOCASE;
            """;

        var list = new List<TagInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new TagInfo(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        return list;
    }

    /// <summary>The tags on the given assets, with how many of those assets carry each one.</summary>
    public async Task<IReadOnlyList<TagCount>> GetTagsForAssetsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return Array.Empty<TagCount>();

        await using var conn = await _db.OpenAsync(ct);
        foreach (var chunk in ids.Chunk(500)) // stay well below SQLite's parameter limit
        {
            await using var cmd = conn.CreateCommand();
            var names = new string[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                names[i] = "$p" + i;
                cmd.Parameters.AddWithValue(names[i], chunk[i]);
            }
            cmd.CommandText =
                "SELECT t.name, COUNT(*) FROM asset_tags xt JOIN tags t ON t.id = xt.tag_id " +
                $"WHERE xt.asset_id IN ({string.Join(",", names)}) GROUP BY t.id, t.name;";

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                counts[name] = counts.GetValueOrDefault(name) + r.GetInt32(1);
            }
        }

        return counts
            .Select(kv => new TagCount(kv.Key, kv.Value))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task AddTagsAsync(IReadOnlyList<long> ids, IEnumerable<string> names, CancellationToken ct = default)
    {
        var clean = names.Select(TagNames.Normalize).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (clean.Count == 0 || ids.Count == 0) return;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var tagIds = new List<long>();
        foreach (var name in clean)
        {
            await using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT OR IGNORE INTO tags (name) VALUES ($name);";
                insert.Parameters.AddWithValue("$name", name);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await using var select = conn.CreateCommand();
            select.Transaction = tx;
            select.CommandText = "SELECT id FROM tags WHERE name = $name;";
            select.Parameters.AddWithValue("$name", name);
            tagIds.Add(Convert.ToInt64(await select.ExecuteScalarAsync(ct)));
        }

        await using var link = conn.CreateCommand();
        link.Transaction = tx;
        link.CommandText = "INSERT OR IGNORE INTO asset_tags (asset_id, tag_id) VALUES ($a, $t);";
        var pA = link.Parameters.Add("$a", SqliteType.Integer);
        var pT = link.Parameters.Add("$t", SqliteType.Integer);
        foreach (var tagId in tagIds)
        {
            pT.Value = tagId;
            foreach (var id in ids)
            {
                pA.Value = id;
                await link.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task RemoveTagAsync(IReadOnlyList<long> ids, string name, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM asset_tags WHERE asset_id = $a AND tag_id = (SELECT id FROM tags WHERE name = $name);";
        cmd.Parameters.AddWithValue("$name", name);
        var pA = cmd.Parameters.Add("$a", SqliteType.Integer);
        foreach (var id in ids)
        {
            pA.Value = id;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Deletes the tag itself and removes it from every asset.</summary>
    public async Task DeleteTagAsync(long tagId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", tagId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------ license / readme documents

    /// <summary>Indexed files in a root whose names look like license or readme files.</summary>
    public async Task<IReadOnlyList<DocCandidate>> GetDocumentCandidatesAsync(long rootId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT directory, relative_path, file_name FROM assets
            WHERE root_id = $root AND is_missing = 0 AND (
                   file_name LIKE 'license%' OR file_name LIKE 'licence%' OR file_name LIKE 'copying%'
                OR file_name LIKE 'unlicense%' OR file_name LIKE 'cc0%' OR file_name LIKE 'readme%'
                OR file_name LIKE 'credits%' OR file_name LIKE 'attribution%' OR file_name LIKE 'notice%');
            """;
        cmd.Parameters.AddWithValue("$root", rootId);

        var list = new List<DocCandidate>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new DocCandidate(r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>Replaces all detected license/readme rows of a root (user overrides live elsewhere and survive).</summary>
    public async Task ReplaceFolderDocsAsync(long rootId, IEnumerable<FolderDocs> rows, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM folder_docs WHERE root_id = $root;";
            delete.Parameters.AddWithValue("$root", rootId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO folder_docs
                (root_id, folder, license_id, license_file, license_text, license_from_readme, readme_file, readme_text)
            VALUES ($root, $folder, $lid, $lfile, $ltext, $lreadme, $rfile, $rtext);
            """;
        var pRoot = cmd.Parameters.Add("$root", SqliteType.Integer);
        var pFolder = cmd.Parameters.Add("$folder", SqliteType.Text);
        var pLid = cmd.Parameters.Add("$lid", SqliteType.Text);
        var pLfile = cmd.Parameters.Add("$lfile", SqliteType.Text);
        var pLtext = cmd.Parameters.Add("$ltext", SqliteType.Text);
        var pLreadme = cmd.Parameters.Add("$lreadme", SqliteType.Integer);
        var pRfile = cmd.Parameters.Add("$rfile", SqliteType.Text);
        var pRtext = cmd.Parameters.Add("$rtext", SqliteType.Text);

        foreach (var row in rows)
        {
            pRoot.Value = row.RootId;
            pFolder.Value = row.Folder;
            pLid.Value = (object?)row.LicenseId ?? DBNull.Value;
            pLfile.Value = (object?)row.LicenseFile ?? DBNull.Value;
            pLtext.Value = (object?)row.LicenseText ?? DBNull.Value;
            pLreadme.Value = row.LicenseFromReadme ? 1 : 0;
            pRfile.Value = (object?)row.ReadmeFile ?? DBNull.Value;
            pRtext.Value = (object?)row.ReadmeText ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<FolderDocsSummary>> GetFolderDocSummariesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT root_id, folder, license_id, license_file, license_from_readme, readme_file FROM folder_docs;";

        var list = new List<FolderDocsSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new FolderDocsSummary(
                r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt32(4) != 0,
                r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public async Task<FolderDocs?> GetFolderDocsAsync(long rootId, string folder, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT license_id, license_file, license_text, license_from_readme, readme_file, readme_text
            FROM folder_docs WHERE root_id = $root AND folder = $folder;
            """;
        cmd.Parameters.AddWithValue("$root", rootId);
        cmd.Parameters.AddWithValue("$folder", folder);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new FolderDocs(
            rootId, folder,
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.GetInt32(3) != 0,
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5));
    }

    public async Task<IReadOnlyList<LicenseOverride>> GetLicenseOverridesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT root_id, folder, license_id FROM license_overrides;";

        var list = new List<LicenseOverride>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new LicenseOverride(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>Assigns a license to a folder by hand; null removes the override (back to detection).</summary>
    public async Task SetLicenseOverrideAsync(long rootId, string folder, string? licenseId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$root", rootId);
        cmd.Parameters.AddWithValue("$folder", folder);
        if (licenseId is null)
        {
            cmd.CommandText = "DELETE FROM license_overrides WHERE root_id = $root AND folder = $folder;";
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO license_overrides (root_id, folder, license_id) VALUES ($root, $folder, $lid)
                ON CONFLICT(root_id, folder) DO UPDATE SET license_id = excluded.license_id;
                """;
            cmd.Parameters.AddWithValue("$lid", licenseId);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------ bundles

    public async Task<IReadOnlyList<BundleInfo>> GetBundlesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.id, b.name, COUNT(a.id)
            FROM bundles b
            LEFT JOIN bundle_assets ba ON ba.bundle_id = b.id
            LEFT JOIN assets a ON a.id = ba.asset_id AND a.is_missing = 0
            GROUP BY b.id, b.name
            ORDER BY b.name COLLATE NOCASE;
            """;

        var list = new List<BundleInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new BundleInfo(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        return list;
    }

    /// <summary>Creates a bundle, or returns the existing one with the same name.</summary>
    public async Task<BundleInfo> CreateBundleAsync(string name, CancellationToken ct = default)
    {
        var clean = BundleNames.Normalize(name);
        if (clean.Length == 0) throw new ArgumentException("A bundle needs a name.", nameof(name));

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO bundles (name, created_utc) VALUES ($name, $now)
            ON CONFLICT(name) DO UPDATE SET name = excluded.name
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$name", clean);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return new BundleInfo(id, clean, 0);
    }

    /// <summary>Returns false if another bundle already has that name.</summary>
    public async Task<bool> RenameBundleAsync(long bundleId, string name, CancellationToken ct = default)
    {
        var clean = BundleNames.Normalize(name);
        if (clean.Length == 0) return false;

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE bundles SET name = $name WHERE id = $id;";
        cmd.Parameters.AddWithValue("$name", clean);
        cmd.Parameters.AddWithValue("$id", bundleId);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE constraint
        {
            return false;
        }
    }

    public async Task DeleteBundleAsync(long bundleId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bundles WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", bundleId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Adds assets to a bundle; returns how many were newly added (the rest were already in it).</summary>
    public async Task<int> AddToBundleAsync(long bundleId, IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return 0;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO bundle_assets (bundle_id, asset_id) VALUES ($b, $a);";
        cmd.Parameters.AddWithValue("$b", bundleId);
        var pA = cmd.Parameters.Add("$a", SqliteType.Integer);

        var added = 0;
        foreach (var id in assetIds)
        {
            pA.Value = id;
            added += await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return added;
    }

    public async Task<int> RemoveFromBundleAsync(long bundleId, IReadOnlyList<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return 0;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM bundle_assets WHERE bundle_id = $b AND asset_id = $a;";
        cmd.Parameters.AddWithValue("$b", bundleId);
        var pA = cmd.Parameters.Add("$a", SqliteType.Integer);

        var removed = 0;
        foreach (var id in assetIds)
        {
            pA.Value = id;
            removed += await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return removed;
    }

    public async Task SetHashAsync(long assetId, string hash, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE assets SET content_hash = $hash WHERE id = $id;";
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$id", assetId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Asset>> SearchAsync(AssetQuery query, CancellationToken ct = default)
    {
        var sql = new StringBuilder($"SELECT {Columns} FROM assets WHERE is_missing = 0");
        if (!string.IsNullOrWhiteSpace(query.Text))
            sql.Append(" AND (relative_path LIKE $text ESCAPE '\\' OR EXISTS (SELECT 1 FROM asset_tags xt JOIN tags tg ON tg.id = xt.tag_id WHERE xt.asset_id = assets.id AND tg.name LIKE $text ESCAPE '\\'))");
        if (query.Type is not null)
            sql.Append(" AND asset_type = $type");
        if (query.RootId is not null)
            sql.Append(" AND root_id = $root");
        var folder = query.Folder?.Trim('/');
        if (!string.IsNullOrEmpty(folder))
            sql.Append(" AND (directory = $folder OR directory LIKE $folderLike ESCAPE '\\')");
        if (query.FavoritesOnly)
            sql.Append(" AND is_favorite = 1");
        if (query.OwnOnly)
            sql.Append(" AND is_own = 1");
        if (query.TagId is not null)
            sql.Append(" AND EXISTS (SELECT 1 FROM asset_tags t1 WHERE t1.asset_id = assets.id AND t1.tag_id = $tagId)");
        if (query.AnyTag)
            sql.Append(" AND EXISTS (SELECT 1 FROM asset_tags t2 WHERE t2.asset_id = assets.id)");
        if (query.BundleId is not null)
            sql.Append(" AND EXISTS (SELECT 1 FROM bundle_assets b1 WHERE b1.asset_id = assets.id AND b1.bundle_id = $bundleId)");
        if (query.AnyBundle)
            sql.Append(" AND EXISTS (SELECT 1 FROM bundle_assets b2 WHERE b2.asset_id = assets.id)");
        sql.Append(" ORDER BY file_name COLLATE NOCASE, id LIMIT $limit OFFSET $offset;");

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var escaped = query.Text.Trim()
                .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$text", $"%{escaped}%");
        }
        if (query.Type is { } type)
            cmd.Parameters.AddWithValue("$type", (int)type);
        if (query.RootId is { } rootFilter)
            cmd.Parameters.AddWithValue("$root", rootFilter);
        if (!string.IsNullOrEmpty(folder))
        {
            cmd.Parameters.AddWithValue("$folder", folder);
            cmd.Parameters.AddWithValue("$folderLike", EscapeLike(folder) + "/%"); // subfolders, but not "tex_old" for "tex"
        }
        if (query.TagId is { } tagId)
            cmd.Parameters.AddWithValue("$tagId", tagId);
        if (query.BundleId is { } bundleId)
            cmd.Parameters.AddWithValue("$bundleId", bundleId);
        cmd.Parameters.AddWithValue("$limit", query.Limit);
        cmd.Parameters.AddWithValue("$offset", query.Offset);

        return await ReadAllAsync(cmd, ct);
    }

    /// <summary>Groups of 2+ present files sharing the same content hash.</summary>
    public async Task<IReadOnlyList<IGrouping<string, Asset>>> FindDuplicatesAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM assets
            WHERE is_missing = 0 AND size_bytes > 0
              AND content_hash IN (
                  SELECT content_hash FROM assets
                  WHERE content_hash IS NOT NULL AND is_missing = 0 AND size_bytes > 0
                  GROUP BY content_hash HAVING COUNT(*) > 1)
            ORDER BY content_hash, relative_path;
            """;

        var rows = await ReadAllAsync(cmd, ct);
        return rows.GroupBy(a => a.ContentHash!).ToList();
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string ResolvePath(string rootPath, string relativePath) =>
        Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static async Task<IReadOnlyList<Asset>> ReadAllAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<Asset>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(Map(r));
        return list;
    }

    private static Asset Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        RootId = r.GetInt64(1),
        RelativePath = r.GetString(2),
        FileName = r.GetString(3),
        Extension = r.GetString(4),
        Type = (AssetType)r.GetInt32(5),
        SizeBytes = r.GetInt64(6),
        ContentHash = r.IsDBNull(7) ? null : r.GetString(7),
        Width = r.IsDBNull(8) ? null : r.GetInt32(8),
        Height = r.IsDBNull(9) ? null : r.GetInt32(9),
        ModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(10)),
        AddedUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(11)),
        IsMissing = r.GetInt32(12) != 0,
        Folder = r.GetString(13),
        IsFavorite = r.GetInt32(14) != 0,
        IsOwn = r.GetInt32(15) != 0,
    };
}
