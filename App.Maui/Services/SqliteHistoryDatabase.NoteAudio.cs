using Microsoft.Data.Sqlite;

namespace App.Maui.Services;

public sealed partial class SqliteHistoryDatabase
{
    public record NoteAudioMetaRow(
        string StorageKey,
        string Id,
        string NotebookId,
        string EntryId,
        string Namespace,
        string ContentType,
        long Size,
        long DurationMs,
        string CreatedAt,
        string? CuesJson);

    public async Task UpsertNoteAudioMetaAsync(NoteAudioMetaRow row, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO note_audio_meta (
                storage_key, id, notebook_id, entry_id, namespace, content_type,
                size, duration_ms, created_at, cues_json)
            VALUES ($key, $id, $nb, $entry, $ns, $ct, $size, $dur, $created, $cues)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                notebook_id = excluded.notebook_id,
                entry_id = excluded.entry_id,
                namespace = excluded.namespace,
                content_type = excluded.content_type,
                size = excluded.size,
                duration_ms = excluded.duration_ms,
                created_at = excluded.created_at,
                cues_json = excluded.cues_json;
            """;
        cmd.Parameters.AddWithValue("$key", row.StorageKey);
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$nb", row.NotebookId);
        cmd.Parameters.AddWithValue("$entry", row.EntryId);
        cmd.Parameters.AddWithValue("$ns", row.Namespace);
        cmd.Parameters.AddWithValue("$ct", row.ContentType);
        cmd.Parameters.AddWithValue("$size", row.Size);
        cmd.Parameters.AddWithValue("$dur", row.DurationMs);
        cmd.Parameters.AddWithValue("$created", row.CreatedAt);
        cmd.Parameters.AddWithValue("$cues", row.CuesJson ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<NoteAudioMetaRow?> GetNoteAudioMetaByIdAsync(string ns, string id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, notebook_id, entry_id, namespace, content_type,
                   size, duration_ms, created_at, cues_json
            FROM note_audio_meta
            WHERE namespace = $ns AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new NoteAudioMetaRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    public async Task PutNoteAudioContentAsync(string storageKey, string content, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO note_audio_content (storage_key, content) VALUES ($key, $content)
            ON CONFLICT(storage_key) DO UPDATE SET content = excluded.content;
            """;
        cmd.Parameters.AddWithValue("$key", storageKey);
        cmd.Parameters.AddWithValue("$content", content);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetNoteAudioContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM note_audio_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<List<string>> ListNoteAudioIdsByNotebookAsync(string ns, string notebookId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM note_audio_meta
            WHERE namespace = $ns AND notebook_id = $nb
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$nb", notebookId);
        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task DeleteNoteAudioAsync(string metaKey, string contentKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "DELETE FROM note_audio_meta WHERE storage_key = $meta";
        cmd.Parameters.AddWithValue("$meta", metaKey);
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.Parameters.Clear();
        cmd.CommandText = "DELETE FROM note_audio_content WHERE storage_key = $content";
        cmd.Parameters.AddWithValue("$content", contentKey);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
