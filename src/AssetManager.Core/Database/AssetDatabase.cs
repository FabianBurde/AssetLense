using Microsoft.Data.Sqlite;

namespace AssetManager.Core.Database;

/// <summary>Owns the SQLite file: creates connections and applies migrations.</summary>
public sealed class AssetDatabase
{
    private readonly string _connectionString;

    public AssetDatabase(string dbPath)
    {
        var full = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>%LOCALAPPDATA%\AssetManager\library.db</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AssetManager", "library.db");

    /// <summary>Opens a connection with the per-connection pragmas applied. Caller disposes it.</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";
            await cmd.ExecuteNonQueryAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates the database if needed and applies any pending migrations. Safe to call on every startup.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        await using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;"; // persistent; readers don't block the scanner
            await wal.ExecuteNonQueryAsync(ct);
        }

        int current;
        await using (var ver = conn.CreateCommand())
        {
            ver.CommandText = "PRAGMA user_version;";
            current = Convert.ToInt32(await ver.ExecuteScalarAsync(ct));
        }

        if (current > Schema.Migrations.Count)
            throw new InvalidOperationException(
                $"Database schema version {current} is newer than this app supports ({Schema.Migrations.Count}).");

        for (var v = current; v < Schema.Migrations.Count; v++)
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Schema.Migrations[v] + $"\nPRAGMA user_version = {v + 1};";
            await cmd.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
    }
}
