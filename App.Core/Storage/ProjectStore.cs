using System.Text.Json;
using System.Text.RegularExpressions;

namespace App.Core.Storage;

public record ProjectFile(string Id, string Name, string Mime, int Size, int Parts, string? ConversationId, string? MessageId);
public record ProjectInfo(string Name, string Instructions, string? Description = null);
public record ProjectWorkflow(string Id, string Name, string Yaml, string? DeviceId);
public record ProjectNoteLink(string NoteId, string? EntryId, string Title, string? ChatId = null);
public record ProjectChatLink(string ChatId);
public record ProjectBlock(string Id, string Name, string Version, byte[] Bytes);

/// <summary>Stable, independently versioned encrypted records. File bytes are immutable; renames update only the header.</summary>
public sealed class ProjectStore(IDocumentStore documents, IStorageQuotaService quota)
{
    public const int PartSize = 256 * 1024, MaxSize = 32 * 1024 * 1024;
    private static readonly SemaphoreSlim Writes = new(1);
    public static string NewId() => "proj:" + Guid.NewGuid().ToString("N");
    public static bool IsProject(string id) => id.StartsWith("proj:") && id.Length == 37;
    public static bool IsCache(string id) => id.EndsWith(":index", StringComparison.Ordinal);
    public static bool Deleted(LibraryDocument d) => d.Source?.StartsWith('Z') == true;
    public static bool IsChat(string id) => id.StartsWith("project-chat:", StringComparison.Ordinal);
    public static string ChatPrefix(string project) => "project-chat:" + project[5..] + ":";
    public static string? ProjectFromChat(string chatId) =>
        IsChat(chatId) ? "proj:" + chatId["project-chat:".Length..].Split(':')[0] : null;

    /// <summary>Ids of existing (non-project) chats linked into a project.</summary>
    public async Task<List<string>> LinkedChatIdsAsync(string project)
    {
        var ids = new List<string>();
        foreach (var row in (await IndexAsync()).Where(r => r.Id.StartsWith(project + ":c:") && !Deleted(r)))
            if (await ReadAsync<ProjectChatLink>(row.Id) is { } l && !string.IsNullOrEmpty(l.ChatId)) ids.Add(l.ChatId);
        return ids;
    }

    /// <summary>All chat ids linked into any project (used to hide them from the general chat list).</summary>
    public async Task<HashSet<string>> AllLinkedChatIdsAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in (await IndexAsync()).Where(r => r.Id.Contains(":c:") && !Deleted(r)))
            if (await ReadAsync<ProjectChatLink>(row.Id) is { } l && !string.IsNullOrEmpty(l.ChatId)) set.Add(l.ChatId);
        return set;
    }
    public Task<List<LibraryDocument>> IndexAsync() => documents.LoadProjectIndexAsync();
    public async Task<T?> ReadAsync<T>(string id) where T : class => await documents.LoadBytesAsync(id) is { Length: > 1 } data ? JsonSerializer.Deserialize<T>(data) : null;
    public Task SaveAsync<T>(string id, string name, T value) => WriteAsync(id, name, JsonSerializer.SerializeToUtf8Bytes(value));
    public Task DeleteAsync(string id) => WriteAsync(id, "Removed", [0], true);
    async Task WriteAsync(string id, string name, byte[] bytes, bool deleted = false)
    {
        var version = (deleted ? "Z" : "V") + DateTime.UtcNow.Ticks.ToString("D19") + Guid.NewGuid().ToString("N");
        await ApplyAsync(new(id, name, version, bytes), local: true);
    }

    public async Task<bool> ApplyAsync(ProjectBlock b, bool local = false)
    {
        if (!Regex.IsMatch(b.Id, @"^proj:[0-9a-f]{32}(?::(?:f|w|n|c):[0-9a-f]{32}(?::(?:[0-9]{1,3}|index))?)?$") || (!local && IsCache(b.Id)) ||
            !Regex.IsMatch(b.Version, "^[VZ][0-9]{19}[0-9a-f]{32}$") || b.Name.Length > 512 || b.Bytes.Length == 0 || (!IsCache(b.Id) && b.Bytes.Length > PartSize))
            throw new InvalidDataException("Invalid project record.");
        await Writes.WaitAsync();
        try
        {
            var rows = await IndexAsync();
            var old = rows.FirstOrDefault(x => x.Id == b.Id);
            if (string.CompareOrdinal(old?.Source, b.Version) >= 0)
            {
                if (!local || Deleted(old!)) return false;
                // A peer's clock can be ahead. A subsequent intentional local edit must still advance the revision.
                var ticks = long.Parse(old!.Source!.AsSpan(1,19)) + 1;
                b = b with { Version = "V" + ticks.ToString("D19") + Guid.NewGuid().ToString("N") };
            }
            if (IsCache(b.Id) && !rows.Any(x=>x.Id==b.Id[..^6] && !Deleted(x))) return false;
            if (b.Bytes.Length > (old?.Size ?? 0) && !await quota.CanAcceptBytesAsync(b.Bytes.Length - (old?.Size ?? 0))) throw new IOException("Device storage quota exceeded.");
            await documents.PutAsync(b.Id, b.Name, "application/octet-stream", b.Bytes, b.Version, null);
            if(b.Version.StartsWith('Z') && b.Id.Contains(":f:") && b.Id.Count(c=>c==':')==3)await documents.DeleteAsync(b.Id+":index");
            return true;
        }
        finally { Writes.Release(); }
    }

    public async Task<ProjectBlock?> ExportAsync(LibraryDocument d)
    {
        await Writes.WaitAsync();
        try { return !IsCache(d.Id) && (await IndexAsync()).Any(r=>r.Id==d.Id && r.Source==d.Source) && await documents.LoadBytesAsync(d.Id) is {} bytes ? new(d.Id, d.Name, d.Source!, bytes) : null; }
        finally {Writes.Release();}
    }

    public async Task<ProjectFile> AddFileAsync(string project, string name, string mime, byte[] data, string? chat = null, string? message = null, string? key = null)
    {
        if (data.Length is 0 or > MaxSize) throw new IOException("Choose a nonempty file of 32 MB or less.");
        if (!await quota.CanAcceptBytesAsync(data.Length)) throw new IOException("Device storage quota exceeded.");
        var id = project + ":f:" + (key ?? Guid.NewGuid().ToString("N"));
        var file = new ProjectFile(id, name, mime, data.Length, (data.Length + PartSize - 1) / PartSize, chat, message);
        try
        {
            await SaveAsync(id, name, file);
            for (int i = 0; i < file.Parts; i++) await WriteAsync(id + ":" + i, "Part", data.AsSpan(i * PartSize, Math.Min(PartSize, data.Length - i * PartSize)).ToArray());
        }
        catch { await RemoveFileAsync(file); throw; }
        return file;
    }

    public async Task<byte[]?> BytesAsync(ProjectFile f)
    {
        if (f.Size is <= 0 or > MaxSize || f.Parts != (f.Size + PartSize - 1) / PartSize) throw new InvalidDataException("Invalid file header.");
        var bytes = new byte[f.Size];
        for (int i = 0; i < f.Parts; i++)
        {
            var part = await documents.LoadBytesAsync(f.Id + ":" + i);
            if (part?.Length != Math.Min(PartSize, f.Size - i * PartSize)) return null;
            part.CopyTo(bytes, i * PartSize);
        }
        return bytes;
    }

    public async Task RemoveFileAsync(ProjectFile f)
    {
        await DeleteAsync(f.Id);
        for (int i = 0; i < f.Parts; i++) await DeleteAsync(f.Id + ":" + i);
        await documents.DeleteAsync(f.Id + ":index");
    }
}
