namespace AssetManager.Core.Database;

/// <summary>
/// Ordered migrations. The database's PRAGMA user_version is the number of scripts already applied.
/// Never edit a script after release; append a new one instead.
/// </summary>
internal static class Schema
{
    public static readonly IReadOnlyList<string> Migrations = new[] { V1, V2, V3, V4, V5 };

    private const string V1 = """
        CREATE TABLE library_roots (
            id           INTEGER PRIMARY KEY,
            path         TEXT NOT NULL UNIQUE COLLATE NOCASE,
            display_name TEXT NOT NULL
        );

        CREATE TABLE assets (
            id            INTEGER PRIMARY KEY,
            root_id       INTEGER NOT NULL REFERENCES library_roots(id) ON DELETE CASCADE,
            relative_path TEXT    NOT NULL COLLATE NOCASE,
            file_name     TEXT    NOT NULL,
            extension     TEXT    NOT NULL,
            asset_type    INTEGER NOT NULL,
            size_bytes    INTEGER NOT NULL,
            content_hash  TEXT,
            width         INTEGER,
            height        INTEGER,
            modified_utc  INTEGER NOT NULL,   -- unix milliseconds
            added_utc     INTEGER NOT NULL,   -- unix milliseconds
            is_missing    INTEGER NOT NULL DEFAULT 0,
            UNIQUE (root_id, relative_path)
        );

        CREATE INDEX ix_assets_hash ON assets(content_hash) WHERE content_hash IS NOT NULL;
        CREATE INDEX ix_assets_type ON assets(asset_type);
        CREATE INDEX ix_assets_name ON assets(file_name COLLATE NOCASE);

        CREATE TABLE tags (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE
        );

        CREATE TABLE asset_tags (
            asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
            tag_id   INTEGER NOT NULL REFERENCES tags(id)   ON DELETE CASCADE,
            PRIMARY KEY (asset_id, tag_id)
        ) WITHOUT ROWID;

        CREATE INDEX ix_asset_tags_tag ON asset_tags(tag_id);
        """;

    // Tracks which scan last saw each file, so files that vanished can be flagged as missing.
    private const string V2 = """
        ALTER TABLE assets ADD COLUMN last_seen INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX ix_assets_root_seen ON assets(root_id, last_seen);
        """;

    // Folder column (for the sidebar tree and folder filtering) and saved copy targets.
    private const string V3 = """
        ALTER TABLE assets ADD COLUMN directory TEXT NOT NULL DEFAULT '' COLLATE NOCASE;

        UPDATE assets SET directory = CASE
            WHEN length(relative_path) > length(file_name)
            THEN substr(relative_path, 1, length(relative_path) - length(file_name) - 1)
            ELSE '' END;

        CREATE INDEX ix_assets_root_dir ON assets(root_id, directory);

        CREATE TABLE quick_targets (
            id           INTEGER PRIMARY KEY,
            name         TEXT    NOT NULL,
            path         TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            is_godot     INTEGER NOT NULL DEFAULT 0,
            sort_by_type INTEGER NOT NULL DEFAULT 0,
            snake_case   INTEGER NOT NULL DEFAULT 0
        );
        """;

    // Favorites, "my creations", and per-folder license/readme information.
    private const string V4 = """
        ALTER TABLE assets ADD COLUMN is_favorite INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE assets ADD COLUMN is_own      INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX ix_assets_favorite ON assets(is_favorite) WHERE is_favorite = 1;
        CREATE INDEX ix_assets_own      ON assets(is_own)      WHERE is_own = 1;

        CREATE TABLE folder_docs (
            root_id             INTEGER NOT NULL REFERENCES library_roots(id) ON DELETE CASCADE,
            folder              TEXT    NOT NULL COLLATE NOCASE,
            license_id          TEXT,
            license_file        TEXT,
            license_text        TEXT,
            license_from_readme INTEGER NOT NULL DEFAULT 0,
            readme_file         TEXT,
            readme_text         TEXT,
            PRIMARY KEY (root_id, folder)
        ) WITHOUT ROWID;

        CREATE TABLE license_overrides (
            root_id    INTEGER NOT NULL REFERENCES library_roots(id) ON DELETE CASCADE,
            folder     TEXT    NOT NULL COLLATE NOCASE,
            license_id TEXT    NOT NULL,
            PRIMARY KEY (root_id, folder)
        ) WITHOUT ROWID;
        """;

    // Bundles: named lists of assets that can be exported as a zip.
    private const string V5 = """
        CREATE TABLE bundles (
            id          INTEGER PRIMARY KEY,
            name        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            created_utc INTEGER NOT NULL
        );

        CREATE TABLE bundle_assets (
            bundle_id INTEGER NOT NULL REFERENCES bundles(id) ON DELETE CASCADE,
            asset_id  INTEGER NOT NULL REFERENCES assets(id)  ON DELETE CASCADE,
            PRIMARY KEY (bundle_id, asset_id)
        ) WITHOUT ROWID;

        CREATE INDEX ix_bundle_assets_asset ON bundle_assets(asset_id);
        """;
}
