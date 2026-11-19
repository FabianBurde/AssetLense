using System.Text;
using AssetManager.Core.Models;
using Microsoft.Data.Sqlite;

namespace AssetManager.Core.Database;

public sealed class AssetRepository
{
    private const string Columns =
        "id, root_id, relative_path, file_name, extension, asset_type, size_bytes, " +
        "content_hash, width, height, modified_utc, added_utc, is_missing, directory";

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
            sql.Append(" AND relative_path LIKE $text ESCAPE '\\'");
        if (query.Type is not null)
            sql.Append(" AND asset_type = $type");
        if (query.RootId is not null)
            sql.Append(" AND root_id = $root");
        var folder = query.Folder?.Trim('/');
        if (!string.IsNullOrEmpty(folder))
            sql.Append(" AND (directory = $folder OR directory LIKE $folderLike ESCAPE '\\')");
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
    };
}
