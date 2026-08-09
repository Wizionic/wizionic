using Microsoft.Data.Sqlite;

namespace App.Maui.Services;

/// <summary>
/// SQLite schema for conversation/note history and settings (shared wizionic_local.db).
/// </summary>
public sealed class SqliteHistoryDatabase
{
    private readonly string _connectionString;
    private bool _initialized;

    public string DatabasePath { get; }

    public SqliteHistoryDatabase()
    {
        DatabasePath = Path.Combine(MauiAppData.Directory, "wizionic_local.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
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

            CREATE TABLE IF NOT EXISTS album_meta (
                storage_key TEXT PRIMARY KEY NOT NULL,
                id TEXT NOT NULL,
                namespace TEXT NOT NULL,
                title TEXT NOT NULL,
                last_updated TEXT NOT NULL,
                sync_enabled INTEGER NOT NULL DEFAULT 0,
                content_fingerprint TEXT,
                deleted_at TEXT,
                is_password_protected INTEGER NOT NULL DEFAULT 0,
                sort_order INTEGER NOT NULL DEFAULT 0,
                protection_changed_ticks INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_album_meta_ns ON album_meta(namespace);
            CREATE INDEX IF NOT EXISTS idx_album_meta_id ON album_meta(id);

            CREATE TABLE IF NOT EXISTS album_content (
                storage_key TEXT PRIMARY KEY NOT NULL,
                content TEXT
            );

            CREATE TABLE IF NOT EXISTS album_image_meta (
                storage_key TEXT PRIMARY KEY NOT NULL,
                id TEXT NOT NULL,
                album_id TEXT NOT NULL,
                namespace TEXT NOT NULL,
                name TEXT NOT NULL,
                content_type TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                width INTEGER,
                height INTEGER,
                last_updated TEXT NOT NULL,
                content_fingerprint TEXT,
                deleted_at TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                thumbnail_base64 TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_album_image_meta_ns ON album_image_meta(namespace);
            CREATE INDEX IF NOT EXISTS idx_album_image_meta_album ON album_image_meta(namespace, album_id);

            CREATE TABLE IF NOT EXISTS album_image_content (
                storage_key TEXT PRIMARY KEY NOT NULL,
                content TEXT
            );

            CREATE TABLE IF NOT EXISTS calendar_meta (
                storage_key TEXT PRIMARY KEY NOT NULL,
                id TEXT NOT NULL,
                namespace TEXT NOT NULL,
                name TEXT NOT NULL,
                color TEXT NOT NULL,
                last_updated TEXT NOT NULL,
                sync_enabled INTEGER NOT NULL DEFAULT 0,
                content_fingerprint TEXT,
                deleted_at TEXT,
                description TEXT,
                time_zone TEXT,
                is_visible INTEGER NOT NULL DEFAULT 1,
                sort_order INTEGER NOT NULL DEFAULT 0,
                is_workflow_calendar INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_calendar_meta_ns ON calendar_meta(namespace);
            CREATE INDEX IF NOT EXISTS idx_calendar_meta_id ON calendar_meta(id);

            CREATE TABLE IF NOT EXISTS event_meta (
                storage_key TEXT PRIMARY KEY NOT NULL,
                id TEXT NOT NULL,
                calendar_id TEXT NOT NULL,
                namespace TEXT NOT NULL,
                summary TEXT NOT NULL,
                start_utc TEXT NOT NULL,
                end_utc TEXT NOT NULL,
                is_all_day INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT 'CONFIRMED',
                last_updated TEXT NOT NULL,
                content_fingerprint TEXT,
                deleted_at TEXT,
                rrule TEXT,
                location TEXT,
                workflow_id TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_event_meta_ns ON event_meta(namespace);
            CREATE INDEX IF NOT EXISTS idx_event_meta_cal ON event_meta(namespace, calendar_id);
            CREATE INDEX IF NOT EXISTS idx_event_meta_range ON event_meta(namespace, start_utc, end_utc);

            CREATE TABLE IF NOT EXISTS event_content (
                storage_key TEXT PRIMARY KEY NOT NULL,
                content TEXT
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Additive migrations for existing local DBs.
        await TryAddColumnAsync(conn, "note_meta", "is_password_protected", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "note_meta", "sort_order", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "note_meta", "protection_changed_ticks", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "conversation_meta", "sort_order", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "conversation_meta", "is_password_protected", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "conversation_meta", "protection_changed_ticks", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "album_meta", "is_password_protected", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "album_meta", "sort_order", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "album_meta", "protection_changed_ticks", "INTEGER NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn, "album_image_meta", "thumbnail_base64", "TEXT", ct);

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
        int SortOrder = 0,
        bool IsPasswordProtected = false,
        long ProtectionChangedTicks = 0);

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
        int SortOrder = 0,
        long ProtectionChangedTicks = 0);

    public record AlbumMetaRow(
        string StorageKey,
        string Id,
        string Namespace,
        string Title,
        string LastUpdated,
        bool SyncEnabled,
        string? ContentFingerprint,
        string? DeletedAt,
        bool IsPasswordProtected = false,
        int SortOrder = 0,
        long ProtectionChangedTicks = 0);

    public record AlbumImageMetaRow(
        string StorageKey,
        string Id,
        string AlbumId,
        string Namespace,
        string Name,
        string ContentType,
        long Size,
        int? Width,
        int? Height,
        string LastUpdated,
        string? ContentFingerprint,
        string? DeletedAt,
        int SortOrder = 0,
        string? ThumbnailBase64 = null);

    public record CalendarMetaRow(
        string StorageKey,
        string Id,
        string Namespace,
        string Name,
        string Color,
        string LastUpdated,
        bool SyncEnabled,
        string? ContentFingerprint,
        string? DeletedAt,
        string? Description = null,
        string? TimeZone = null,
        bool IsVisible = true,
        int SortOrder = 0,
        bool IsWorkflowCalendar = false);

    public record EventMetaRow(
        string StorageKey,
        string Id,
        string CalendarId,
        string Namespace,
        string Summary,
        string StartUtc,
        string EndUtc,
        bool IsAllDay,
        string Status,
        string LastUpdated,
        string? ContentFingerprint,
        string? DeletedAt,
        string? RRule = null,
        string? Location = null,
        string? WorkflowId = null);

    public async Task<List<ConvoMetaRow>> GetConvoMetasByNamespaceAsync(string ns, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, title, last_updated, sync_enabled,
                   content_fingerprint, deleted_at, title_is_custom, sort_order, is_password_protected,
                   protection_changed_ticks
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
                   content_fingerprint, deleted_at, title_is_custom, sort_order, is_password_protected,
                   protection_changed_ticks
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
                content_fingerprint, deleted_at, title_is_custom, sort_order, is_password_protected,
                protection_changed_ticks)
            VALUES ($key, $id, $ns, $title, $last, $sync, $fp, $deleted, $custom, $sort, $locked, $proticks)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                namespace = excluded.namespace,
                title = excluded.title,
                last_updated = excluded.last_updated,
                sync_enabled = excluded.sync_enabled,
                content_fingerprint = excluded.content_fingerprint,
                deleted_at = excluded.deleted_at,
                title_is_custom = excluded.title_is_custom,
                sort_order = excluded.sort_order,
                is_password_protected = excluded.is_password_protected,
                protection_changed_ticks = excluded.protection_changed_ticks;
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
                   content_fingerprint, deleted_at, is_password_protected, sort_order,
                   protection_changed_ticks
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
                   content_fingerprint, deleted_at, is_password_protected, sort_order,
                   protection_changed_ticks
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
                content_fingerprint, deleted_at, is_password_protected, sort_order,
                protection_changed_ticks)
            VALUES ($key, $id, $ns, $title, $last, $sync, $fp, $deleted, $locked, $sort, $proticks)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                namespace = excluded.namespace,
                title = excluded.title,
                last_updated = excluded.last_updated,
                sync_enabled = excluded.sync_enabled,
                content_fingerprint = excluded.content_fingerprint,
                deleted_at = excluded.deleted_at,
                is_password_protected = excluded.is_password_protected,
                sort_order = excluded.sort_order,
                protection_changed_ticks = excluded.protection_changed_ticks;
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

    public async Task<List<AlbumMetaRow>> GetAlbumMetasByNamespaceAsync(string ns, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, title, last_updated, sync_enabled,
                   content_fingerprint, deleted_at, is_password_protected, sort_order,
                   protection_changed_ticks
            FROM album_meta
            WHERE namespace = $ns
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        return await ReadAlbumMetasAsync(cmd, ct);
    }

    public async Task<AlbumMetaRow?> GetAlbumMetaByIdAsync(string ns, string id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, title, last_updated, sync_enabled,
                   content_fingerprint, deleted_at, is_password_protected, sort_order,
                   protection_changed_ticks
            FROM album_meta
            WHERE namespace = $ns AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await ReadAlbumMetasAsync(cmd, ct);
        return rows.FirstOrDefault();
    }

    public async Task UpsertAlbumMetaAsync(AlbumMetaRow row, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO album_meta (
                storage_key, id, namespace, title, last_updated, sync_enabled,
                content_fingerprint, deleted_at, is_password_protected, sort_order,
                protection_changed_ticks)
            VALUES ($key, $id, $ns, $title, $last, $sync, $fp, $deleted, $locked, $sort, $proticks)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                namespace = excluded.namespace,
                title = excluded.title,
                last_updated = excluded.last_updated,
                sync_enabled = excluded.sync_enabled,
                content_fingerprint = excluded.content_fingerprint,
                deleted_at = excluded.deleted_at,
                is_password_protected = excluded.is_password_protected,
                sort_order = excluded.sort_order,
                protection_changed_ticks = excluded.protection_changed_ticks;
            """;
        BindAlbumMeta(cmd, row);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetAlbumContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM album_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetAlbumContentAsync(string storageKey, string? content, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO album_content (storage_key, content) VALUES ($key, $content)
            ON CONFLICT(storage_key) DO UPDATE SET content = excluded.content;
            """;
        cmd.Parameters.AddWithValue("$key", storageKey);
        cmd.Parameters.AddWithValue("$content", content ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAlbumContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM album_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<AlbumImageMetaRow>> GetAlbumImageMetasByNamespaceAsync(string ns, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, album_id, namespace, name, content_type, size, width, height,
                   last_updated, content_fingerprint, deleted_at, sort_order, thumbnail_base64
            FROM album_image_meta WHERE namespace = $ns
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        return await ReadAlbumImageMetasAsync(cmd, ct);
    }

    public async Task<List<AlbumImageMetaRow>> GetAlbumImageMetasByAlbumAsync(string ns, string albumId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, album_id, namespace, name, content_type, size, width, height,
                   last_updated, content_fingerprint, deleted_at, sort_order, thumbnail_base64
            FROM album_image_meta WHERE namespace = $ns AND album_id = $album
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$album", albumId);
        return await ReadAlbumImageMetasAsync(cmd, ct);
    }

    public async Task<AlbumImageMetaRow?> GetAlbumImageMetaAsync(string ns, string albumId, string imageId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, album_id, namespace, name, content_type, size, width, height,
                   last_updated, content_fingerprint, deleted_at, sort_order, thumbnail_base64
            FROM album_image_meta WHERE namespace = $ns AND album_id = $album AND id = $id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$album", albumId);
        cmd.Parameters.AddWithValue("$id", imageId);
        var rows = await ReadAlbumImageMetasAsync(cmd, ct);
        return rows.FirstOrDefault();
    }

    public async Task UpsertAlbumImageMetaAsync(AlbumImageMetaRow row, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO album_image_meta (
                storage_key, id, album_id, namespace, name, content_type, size, width, height,
                last_updated, content_fingerprint, deleted_at, sort_order, thumbnail_base64)
            VALUES ($key, $id, $album, $ns, $name, $ct, $size, $w, $h, $last, $fp, $deleted, $sort, $thumb)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                album_id = excluded.album_id,
                namespace = excluded.namespace,
                name = excluded.name,
                content_type = excluded.content_type,
                size = excluded.size,
                width = excluded.width,
                height = excluded.height,
                last_updated = excluded.last_updated,
                content_fingerprint = excluded.content_fingerprint,
                deleted_at = excluded.deleted_at,
                sort_order = excluded.sort_order,
                thumbnail_base64 = excluded.thumbnail_base64;
            """;
        cmd.Parameters.AddWithValue("$key", row.StorageKey);
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$album", row.AlbumId);
        cmd.Parameters.AddWithValue("$ns", row.Namespace);
        cmd.Parameters.AddWithValue("$name", row.Name);
        cmd.Parameters.AddWithValue("$ct", row.ContentType);
        cmd.Parameters.AddWithValue("$size", row.Size);
        cmd.Parameters.AddWithValue("$w", row.Width.HasValue ? row.Width.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$h", row.Height.HasValue ? row.Height.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$last", row.LastUpdated);
        cmd.Parameters.AddWithValue("$fp", row.ContentFingerprint ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$deleted", row.DeletedAt ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$sort", row.SortOrder);
        cmd.Parameters.AddWithValue("$thumb", row.ThumbnailBase64 ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAlbumImageMetaAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM album_image_meta WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetAlbumImageContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM album_image_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetAlbumImageContentAsync(string storageKey, string? content, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO album_image_content (storage_key, content) VALUES ($key, $content)
            ON CONFLICT(storage_key) DO UPDATE SET content = excluded.content;
            """;
        cmd.Parameters.AddWithValue("$key", storageKey);
        cmd.Parameters.AddWithValue("$content", content ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAlbumImageContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM album_image_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Sum LENGTH(content) for encrypted blobs by table (all namespaces).</summary>
    public async Task<long> SumContentLengthAsync(string table, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        // Only allow known content tables
        var allowed = table switch
        {
            "conversation_content" => "conversation_content",
            "note_content" => "note_content",
            "album_content" => "album_content",
            "album_image_content" => "album_image_content",
            _ => null
        };
        if (allowed == null) return 0;
        cmd.CommandText = $"SELECT COALESCE(SUM(LENGTH(content)), 0) FROM {allowed}";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : Convert.ToInt64(result ?? 0);
    }

    /// <summary>Delete legacy whole-album blobs (pre per-image storage).</summary>
    public async Task PurgeLegacyAlbumContentAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM album_content";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task VacuumAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<AlbumImageMetaRow>> ReadAlbumImageMetasAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var rows = new List<AlbumImageMetaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int? width = reader.IsDBNull(7) ? null : (int)reader.GetInt64(7);
            int? height = reader.IsDBNull(8) ? null : (int)reader.GetInt64(8);
            var sort = reader.FieldCount > 12 && !reader.IsDBNull(12) ? (int)reader.GetInt64(12) : 0;
            string? thumb = reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetString(13) : null;
            rows.Add(new AlbumImageMetaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                width,
                height,
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                sort,
                thumb));
        }
        return rows;
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
        cmd.Parameters.AddWithValue("$locked", row.IsPasswordProtected ? 1 : 0);
        cmd.Parameters.AddWithValue("$proticks", row.ProtectionChangedTicks);
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
        cmd.Parameters.AddWithValue("$proticks", row.ProtectionChangedTicks);
    }

    private static void BindAlbumMeta(SqliteCommand cmd, AlbumMetaRow row)
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
        cmd.Parameters.AddWithValue("$proticks", row.ProtectionChangedTicks);
    }

    private static async Task<List<ConvoMetaRow>> ReadConvoMetasAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var rows = new List<ConvoMetaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sortOrder = reader.FieldCount > 9 && !reader.IsDBNull(9) ? (int)reader.GetInt64(9) : 0;
            var isProtected = reader.FieldCount > 10 && !reader.IsDBNull(10) && reader.GetInt64(10) != 0;
            var proticks = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetInt64(11) : 0L;
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
                sortOrder,
                isProtected,
                proticks));
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
            var proticks = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetInt64(10) : 0L;
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
                sortOrder,
                proticks));
        }

        return rows;
    }

    private static async Task<List<AlbumMetaRow>> ReadAlbumMetasAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var rows = new List<AlbumMetaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var isProtected = reader.FieldCount > 8 && !reader.IsDBNull(8) && reader.GetInt64(8) != 0;
            var sortOrder = reader.FieldCount > 9 && !reader.IsDBNull(9) ? (int)reader.GetInt64(9) : 0;
            var proticks = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetInt64(10) : 0L;
            rows.Add(new AlbumMetaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                isProtected,
                sortOrder,
                proticks));
        }

        return rows;
    }

    // ── Calendar ───────────────────────────────────────────────────────────

    public async Task<List<CalendarMetaRow>> GetCalendarMetasByNamespaceAsync(string ns, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, name, color, last_updated, sync_enabled,
                   content_fingerprint, deleted_at, description, time_zone, is_visible, sort_order, is_workflow_calendar
            FROM calendar_meta WHERE namespace = $ns
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        return await ReadCalendarMetasAsync(cmd, ct);
    }

    public async Task<CalendarMetaRow?> GetCalendarMetaByIdAsync(string ns, string id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, name, color, last_updated, sync_enabled,
                   content_fingerprint, deleted_at, description, time_zone, is_visible, sort_order, is_workflow_calendar
            FROM calendar_meta WHERE namespace = $ns AND id = $id
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await ReadCalendarMetasAsync(cmd, ct);
        return rows.FirstOrDefault();
    }

    public async Task UpsertCalendarMetaAsync(CalendarMetaRow row, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO calendar_meta (
                storage_key, id, namespace, name, color, last_updated, sync_enabled,
                content_fingerprint, deleted_at, description, time_zone, is_visible, sort_order, is_workflow_calendar)
            VALUES ($key, $id, $ns, $name, $color, $last, $sync, $fp, $deleted, $desc, $tz, $vis, $sort, $wf)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                namespace = excluded.namespace,
                name = excluded.name,
                color = excluded.color,
                last_updated = excluded.last_updated,
                sync_enabled = excluded.sync_enabled,
                content_fingerprint = excluded.content_fingerprint,
                deleted_at = excluded.deleted_at,
                description = excluded.description,
                time_zone = excluded.time_zone,
                is_visible = excluded.is_visible,
                sort_order = excluded.sort_order,
                is_workflow_calendar = excluded.is_workflow_calendar;
            """;
        cmd.Parameters.AddWithValue("$key", row.StorageKey);
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$ns", row.Namespace);
        cmd.Parameters.AddWithValue("$name", row.Name);
        cmd.Parameters.AddWithValue("$color", row.Color);
        cmd.Parameters.AddWithValue("$last", row.LastUpdated);
        cmd.Parameters.AddWithValue("$sync", row.SyncEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$fp", (object?)row.ContentFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deleted", (object?)row.DeletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", (object?)row.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tz", (object?)row.TimeZone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$vis", row.IsVisible ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort", row.SortOrder);
        cmd.Parameters.AddWithValue("$wf", row.IsWorkflowCalendar ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<EventMetaRow>> GetEventMetasByNamespaceAsync(string ns, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, calendar_id, namespace, summary, start_utc, end_utc, is_all_day,
                   status, last_updated, content_fingerprint, deleted_at, rrule, location, workflow_id
            FROM event_meta WHERE namespace = $ns
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        return await ReadEventMetasAsync(cmd, ct);
    }

    public async Task<EventMetaRow?> GetEventMetaByIdAsync(string ns, string id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, calendar_id, namespace, summary, start_utc, end_utc, is_all_day,
                   status, last_updated, content_fingerprint, deleted_at, rrule, location, workflow_id
            FROM event_meta WHERE namespace = $ns AND id = $id
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$id", id);
        var rows = await ReadEventMetasAsync(cmd, ct);
        return rows.FirstOrDefault();
    }

    public async Task UpsertEventMetaAsync(EventMetaRow row, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO event_meta (
                storage_key, id, calendar_id, namespace, summary, start_utc, end_utc, is_all_day,
                status, last_updated, content_fingerprint, deleted_at, rrule, location, workflow_id)
            VALUES ($key, $id, $cal, $ns, $summary, $start, $end, $allday, $status, $last, $fp, $deleted, $rrule, $loc, $wf)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                calendar_id = excluded.calendar_id,
                namespace = excluded.namespace,
                summary = excluded.summary,
                start_utc = excluded.start_utc,
                end_utc = excluded.end_utc,
                is_all_day = excluded.is_all_day,
                status = excluded.status,
                last_updated = excluded.last_updated,
                content_fingerprint = excluded.content_fingerprint,
                deleted_at = excluded.deleted_at,
                rrule = excluded.rrule,
                location = excluded.location,
                workflow_id = excluded.workflow_id;
            """;
        cmd.Parameters.AddWithValue("$key", row.StorageKey);
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$cal", row.CalendarId);
        cmd.Parameters.AddWithValue("$ns", row.Namespace);
        cmd.Parameters.AddWithValue("$summary", row.Summary);
        cmd.Parameters.AddWithValue("$start", row.StartUtc);
        cmd.Parameters.AddWithValue("$end", row.EndUtc);
        cmd.Parameters.AddWithValue("$allday", row.IsAllDay ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", row.Status);
        cmd.Parameters.AddWithValue("$last", row.LastUpdated);
        cmd.Parameters.AddWithValue("$fp", (object?)row.ContentFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deleted", (object?)row.DeletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rrule", (object?)row.RRule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$loc", (object?)row.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wf", (object?)row.WorkflowId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetEventContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM event_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetEventContentAsync(string storageKey, string? content, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO event_content (storage_key, content) VALUES ($key, $content)
            ON CONFLICT(storage_key) DO UPDATE SET content = excluded.content;
            """;
        cmd.Parameters.AddWithValue("$key", storageKey);
        cmd.Parameters.AddWithValue("$content", content ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteEventContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM event_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<CalendarMetaRow>> ReadCalendarMetasAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var rows = new List<CalendarMetaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CalendarMetaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) || reader.GetInt64(11) != 0,
                reader.IsDBNull(12) ? 0 : (int)reader.GetInt64(12),
                !reader.IsDBNull(13) && reader.GetInt64(13) != 0));
        }
        return rows;
    }

    private static async Task<List<EventMetaRow>> ReadEventMetasAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var rows = new List<EventMetaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new EventMetaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7) != 0,
                reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return rows;
    }
}