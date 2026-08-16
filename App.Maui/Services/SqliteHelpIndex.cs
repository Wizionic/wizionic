using System.Globalization;
using App.Core.Help;
using App.Shared.Services.Help;
using Microsoft.Data.Sqlite;

namespace App.Maui.Services;

/// <summary>
/// Help RAG cache in a dedicated SQLite file (not wizionic_local.db).
/// Stores chunk text plus embedding blobs; cosine search runs in process.
/// Optionally loads sqlite-vec (vec0) when the native library is beside the app.
/// </summary>
public sealed class SqliteHelpIndex : IHelpIndex
{
    private readonly string _path;
    private readonly string _connectionString;

    public SqliteHelpIndex()
    {
        Directory.CreateDirectory(MauiAppData.Directory);
        _path = Path.Combine(MauiAppData.Directory, "help_rag.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _path }.ToString();
    }

    public async Task<HelpIndexStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT catalog_hash, embed_model, dimensions, built_at,
                   (SELECT COUNT(*) FROM help_chunks) AS chunks,
                   (SELECT COUNT(*) FROM help_chunks WHERE embedding IS NOT NULL) AS vecs
            FROM help_meta WHERE id = 1
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new HelpIndexStatus();

        var chunks = reader.GetInt32(4);
        var vecs = reader.GetInt32(5);
        DateTimeOffset? built = null;
        if (!reader.IsDBNull(3) && DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            built = parsed;

        return new HelpIndexStatus
        {
            Ready = chunks > 0,
            ChunkCount = chunks,
            HasVectors = vecs == chunks && chunks > 0,
            CatalogHash = reader.IsDBNull(0) ? null : reader.GetString(0),
            EmbedModelId = reader.IsDBNull(1) ? null : reader.GetString(1),
            Dimensions = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            BuiltAtUtc = built
        };
    }

    public async Task RebuildAsync(
        IReadOnlyList<HelpChunk> chunks,
        string catalogHash,
        string? embedModelId,
        int dimensions,
        IReadOnlyList<float[]>? embeddings,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await ExecAsync(conn, "DELETE FROM help_chunks", ct);
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO help_chunks(id, topic_id, title, anchor, text, embedding)
                    VALUES ($id, $topic, $title, $anchor, $text, $emb)
                    """;
                var pId = ins.Parameters.Add("$id", SqliteType.Integer);
                var pTopic = ins.Parameters.Add("$topic", SqliteType.Text);
                var pTitle = ins.Parameters.Add("$title", SqliteType.Text);
                var pAnchor = ins.Parameters.Add("$anchor", SqliteType.Text);
                var pText = ins.Parameters.Add("$text", SqliteType.Text);
                var pEmb = ins.Parameters.Add("$emb", SqliteType.Blob);

                for (var i = 0; i < chunks.Count; i++)
                {
                    var c = chunks[i];
                    pId.Value = c.Id;
                    pTopic.Value = c.TopicId;
                    pTitle.Value = c.Title;
                    pAnchor.Value = (object?)c.Anchor ?? DBNull.Value;
                    pText.Value = c.Text;
                    pEmb.Value = embeddings != null && i < embeddings.Count
                        ? Pack(embeddings[i])
                        : DBNull.Value;
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }

            await using (var meta = conn.CreateCommand())
            {
                meta.Transaction = tx;
                meta.CommandText = """
                    INSERT INTO help_meta(id, catalog_hash, embed_model, dimensions, built_at)
                    VALUES (1, $hash, $model, $dim, $built)
                    ON CONFLICT(id) DO UPDATE SET
                        catalog_hash = excluded.catalog_hash,
                        embed_model = excluded.embed_model,
                        dimensions = excluded.dimensions,
                        built_at = excluded.built_at
                    """;
                meta.Parameters.AddWithValue("$hash", catalogHash);
                meta.Parameters.AddWithValue("$model", (object?)embedModelId ?? DBNull.Value);
                meta.Parameters.AddWithValue("$dim", dimensions);
                meta.Parameters.AddWithValue("$built", DateTimeOffset.UtcNow.ToString("O"));
                await meta.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<HelpSearchHit>> SearchVectorAsync(float[] query, int k, CancellationToken ct = default)
    {
        if (query.Length == 0)
            return Array.Empty<HelpSearchHit>();

        await using var conn = await OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, topic_id, title, anchor, text, embedding FROM help_chunks WHERE embedding IS NOT NULL";
        var hits = new List<HelpSearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var blob = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5);
            if (blob == null || blob.Length < 4)
                continue;
            var vec = Unpack(blob);
            hits.Add(new HelpSearchHit
            {
                Chunk = new HelpChunk
                {
                    Id = reader.GetInt32(0),
                    TopicId = reader.GetString(1),
                    Title = reader.GetString(2),
                    Anchor = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Text = reader.GetString(4)
                },
                Score = MemoryHelpIndex.Cosine(query, vec)
            });
        }

        return hits.OrderByDescending(h => h.Score).Take(Math.Max(1, k)).ToList();
    }

    public async Task<IReadOnlyList<HelpChunk>> GetChunksAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, topic_id, title, anchor, text FROM help_chunks ORDER BY id";
        var list = new List<HelpChunk>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new HelpChunk
            {
                Id = reader.GetInt32(0),
                TopicId = reader.GetString(1),
                Title = reader.GetString(2),
                Anchor = reader.IsDBNull(3) ? null : reader.GetString(3),
                Text = reader.GetString(4)
            });
        }

        return list;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        TryLoadSqliteVec(conn);
        return conn;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS help_meta (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                catalog_hash TEXT,
                embed_model TEXT,
                dimensions INTEGER,
                built_at TEXT
            );
            CREATE TABLE IF NOT EXISTS help_chunks (
                id INTEGER PRIMARY KEY NOT NULL,
                topic_id TEXT NOT NULL,
                title TEXT NOT NULL,
                anchor TEXT,
                text TEXT NOT NULL,
                embedding BLOB
            );
            """, ct);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void TryLoadSqliteVec(SqliteConnection conn)
    {
        try
        {
            conn.EnableExtensions();
            var name = OperatingSystem.IsWindows() ? "vec0.dll" : "vec0.so";
            var candidate = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(candidate))
                conn.LoadExtension(candidate);
        }
        catch
        {
            // Cosine-over-blobs still works.
        }
    }

    private static byte[] Pack(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] Unpack(byte[] bytes)
    {
        var values = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 4);
        return values;
    }
}
