using Microsoft.Data.Sqlite;

namespace App.Maui.Services;

public sealed partial class SqliteHistoryDatabase
{
    public record DocumentMetaRow(
        string StorageKey,
        string Id,
        string Namespace,
        string Name,
        string ContentType,
        long Size,
        string LastUpdated,
        string? Source,
        string? ConversationId);

    public async Task<List<DocumentMetaRow>> GetDocumentMetasByNamespaceAsync(string ns, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, name, content_type, size, last_updated, source, conversation_id
            FROM document_meta WHERE namespace = $ns
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        var list = new List<DocumentMetaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DocumentMetaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return list;
    }

    public async Task<DocumentMetaRow?> GetDocumentMetaAsync(string ns, string id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT storage_key, id, namespace, name, content_type, size, last_updated, source, conversation_id
            FROM document_meta WHERE namespace = $ns AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ns", ns);
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new DocumentMetaRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    public async Task PutDocumentAsync(DocumentMetaRow row, string contentKey, string content, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var transaction = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO document_content (storage_key, content) VALUES ($contentKey, $content)
            ON CONFLICT(storage_key) DO UPDATE SET content = excluded.content;
            INSERT INTO document_meta (
                storage_key, id, namespace, name, content_type, size, last_updated, source, conversation_id)
            VALUES ($key, $id, $ns, $name, $ct, $size, $last, $source, $convo)
            ON CONFLICT(storage_key) DO UPDATE SET
                id = excluded.id,
                namespace = excluded.namespace,
                name = excluded.name,
                content_type = excluded.content_type,
                size = excluded.size,
                last_updated = excluded.last_updated,
                source = excluded.source,
                conversation_id = excluded.conversation_id;
            """;
        cmd.Parameters.AddWithValue("$contentKey", contentKey);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$key", row.StorageKey);
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.Parameters.AddWithValue("$ns", row.Namespace);
        cmd.Parameters.AddWithValue("$name", row.Name);
        cmd.Parameters.AddWithValue("$ct", row.ContentType);
        cmd.Parameters.AddWithValue("$size", row.Size);
        cmd.Parameters.AddWithValue("$last", row.LastUpdated);
        cmd.Parameters.AddWithValue("$source", row.Source ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$convo", row.ConversationId ?? (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteDocumentMetaAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM document_meta WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetDocumentContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM document_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task DeleteDocumentContentAsync(string storageKey, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM document_content WHERE storage_key = $key";
        cmd.Parameters.AddWithValue("$key", storageKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
