using Microsoft.Data.Sqlite;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// SQLite schema for conversation/note history and settings (shared chatfish_local.db).
/// </summary>
public sealed class SqliteHistoryDatabase
{
    private readonly string _connectionString;
    private bool _initialized;

    public SqliteHistoryDatabase()
    {
        var dbPath = Path.Combine(MauiAppData.Directory, "chatfish_local.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY NOT NULL,
                value TEXT
            );

            CREATE TABLE IF NOT EXISTS conversation_meta (
                storage_key TEXT PRIMARY KEY NOT NULL,
                id TEXT NOT NULL,
                namespace TEXT NOT NULL,
                title TEXT NOT NULL,
                last_updated TEXT NOT NULL,
                sync_enabled INTEGER NOT NULL DEFAULT 0,
                content_fingerprint TEXT,
                deleted_at TEXT,
                title_is_custom INTEGER NOT NULL DEFAULT 0,
                sort_order INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_conversation_meta_ns ON conversation_meta(namespace);
            CREATE INDEX IF NOT EXISTS idx_conversation_meta_id ON conversation_meta(id);

            CREATE TABLE IF NOT EXISTS conversation_content (
                storage_key TEXT PRIMARY KEY NOT NULL,
                content TEXT
            );

            CREATE TABLE IF NOT EXISTS note_meta (
                storage_key TEXT PRIMARY KEY NOT NULL,
                id TEXT NOT NULL,
                namespace TEXT NOT NULL,
                title TEXT NOT NULL,
                last_updated TEXT NOT NULL,
                sync_enabled INTEGER NOT NULL DEFAULT 0,
                content_fingerprint TEXT,
                deleted_at TEXT,
                is_password_protected INTEGER NOT NULL DEFAULT 0,
                sort_order INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_note_meta_ns ON note_meta(namespace);
            CREATE INDEX IF NOT EXISTS idx_note_meta_id ON note_meta(id);

            CREATE TABLE IF NOT EXISTS note_content (
                storage_key TEXT PRIMARY KEY NOT NULL,
                content TEXT
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Additive migrations for existing local DBs.
        await TryAddColumnAsync(conn, "note_meta", "is_password_protected", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "note_meta", "sort_order", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "conversation_meta", "sort_order", "INTEGER NOT NULL DEFAULT 0", ct);

        _initialized = true;
    }

    private static async Task TryAddColumnAsync(SqliteConnection conn, string table, string column, string typeSql, CancellationToken ct)
    {
        var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        var hasColumn = false;
        await using (var reader = await check.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
            return;

        var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeSql}";
        await alter.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetSettingAsync(string key, string? value, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public record ConvoMetaRow(
        string StorageKey,
        string Id,
        string Namespace,
        string Title,
        string LastUpdated,
        bool SyncEnabled,
        string? ContentFingerprint,
        string? DeletedAt,
        bool TitleIsCustom,
        int SortOrder = 0);

    public record NoteMetaRow(
        string StorageKey,
        string Id,
        string Namespace,
        string Title,
        string LastUpdated,
        bool SyncEnabled,
        string? ContentFingerprint,
        string? DeletedAt,
        bool IsPasswordProtected = false,
        int SortOrder = 0);

    public async Task<List<ConvoMetaRow>> GetConvoMetasByNamespaceAsync(string ns, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, title, last_updated, sync_enabled,
                   content_fingerprint, deleted_at, title_is_custom, sort_order
            FROM conversation_meta
            WHERE namespace = $ns
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        return await ReadConvoMetasAsync(cmd, ct);
    }

    public async Task<ConvoMetaRow?> GetConvoMetaByIdAsync(string ns, string id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, title, last_updated, sync_enabled,
                   content_fingerprint, deleted_at, title_is_custom, sort_order
            FROM conversation_meta
            WHERE namespace = $ns AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await ReadConvoMetasAsync(cmd, ct);
        return rows.FirstOrDefault();
    }

    public async Task UpsertConvoMetaAsync(ConvoMetaRow row, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversation_meta (
                storage_key, id, namespace, title, last_updated, sync_enabled,
                content_fingerprint, deleted_at, title_is_custom, sort_order)
            VALUES ($key, $id, $ns, $title, $last, $sync, $fp, $deleted, $custom, $sort)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                namespace = excluded.namespace,
                title = excluded.title,
                last_updated = excluded.last_updated,
                sync_enabled = excluded.sync_enabled,
                content_fingerprint = excluded.content_fingerprint,
                deleted_at = excluded.deleted_at,
                title_is_custom = excluded.title_is_custom,
                sort_order = excluded.sort_order;
            """;
        BindConvoMeta(cmd, row);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetConvoContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM conversation_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetConvoContentAsync(string storageKey, string? content, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversation_content (storage_key, content) VALUES ($key, $content)
            ON CONFLICT(storage_key) DO UPDATE SET content = excluded.content;
            """;
        cmd.Parameters.AddWithValue("$key", storageKey);
        cmd.Parameters.AddWithValue("$content", content ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteConvoContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM conversation_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<NoteMetaRow>> GetNoteMetasByNamespaceAsync(string ns, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, title, last_updated, sync_enabled,
                   content_fingerprint, deleted_at, is_password_protected, sort_order
            FROM note_meta
            WHERE namespace = $ns
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        return await ReadNoteMetasAsync(cmd, ct);
    }

    public async Task<NoteMetaRow?> GetNoteMetaByIdAsync(string ns, string id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, title, last_updated, sync_enabled,
                   content_fingerprint, deleted_at, is_password_protected, sort_order
            FROM note_meta
            WHERE namespace = $ns AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await ReadNoteMetasAsync(cmd, ct);
        return rows.FirstOrDefault();
    }

    public async Task UpsertNoteMetaAsync(NoteMetaRow row, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO note_meta (
                storage_key, id, namespace, title, last_updated, sync_enabled,
                content_fingerprint, deleted_at, is_password_protected, sort_order)
            VALUES ($key, $id, $ns, $title, $last, $sync, $fp, $deleted, $locked, $sort)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                namespace = excluded.namespace,
                title = excluded.title,
                last_updated = excluded.last_updated,
                sync_enabled = excluded.sync_enabled,
                content_fingerprint = excluded.content_fingerprint,
                deleted_at = excluded.deleted_at,
                is_password_protected = excluded.is_password_protected,
                sort_order = excluded.sort_order;
            """;
        BindNoteMeta(cmd, row);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetNoteContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM note_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetNoteContentAsync(string storageKey, string? content, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO note_content (storage_key, content) VALUES ($key, $content)
            ON CONFLICT(storage_key) DO UPDATE SET content = excluded.content;
            """;
        cmd.Parameters.AddWithValue("$key", storageKey);
        cmd.Parameters.AddWithValue("$content", content ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteNoteContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM note_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void BindConvoMeta(SqliteCommand cmd, ConvoMetaRow row)
    {
        cmd.Parameters.AddWithValue("$key", row.StorageKey);
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$ns", row.Namespace);
        cmd.Parameters.AddWithValue("$title", row.Title);
        cmd.Parameters.AddWithValue("$last", row.LastUpdated);
        cmd.Parameters.AddWithValue("$sync", row.SyncEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$fp", row.ContentFingerprint ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$deleted", row.DeletedAt ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$custom", row.TitleIsCustom ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort", row.SortOrder);
    }

    private static void BindNoteMeta(SqliteCommand cmd, NoteMetaRow row)
    {
        cmd.Parameters.AddWithValue("$key", row.StorageKey);
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$ns", row.Namespace);
        cmd.Parameters.AddWithValue("$title", row.Title);
        cmd.Parameters.AddWithValue("$last", row.LastUpdated);
        cmd.Parameters.AddWithValue("$sync", row.SyncEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$fp", row.ContentFingerprint ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$deleted", row.DeletedAt ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$locked", row.IsPasswordProtected ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort", row.SortOrder);
    }

    private static async Task<List<ConvoMetaRow>> ReadConvoMetasAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var rows = new List<ConvoMetaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sortOrder = reader.FieldCount > 9 && !reader.IsDBNull(9) ? (int)reader.GetInt64(9) : 0;
            rows.Add(new ConvoMetaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8) != 0,
                sortOrder));
        }

        return rows;
    }

    private static async Task<List<NoteMetaRow>> ReadNoteMetasAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var rows = new List<NoteMetaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var isProtected = reader.FieldCount > 8 && !reader.IsDBNull(8) && reader.GetInt64(8) != 0;
            var sortOrder = reader.FieldCount > 9 && !reader.IsDBNull(9) ? (int)reader.GetInt64(9) : 0;
            rows.Add(new NoteMetaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                isProtected,
                sortOrder));
        }

        return rows;
    }
}