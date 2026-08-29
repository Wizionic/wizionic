namespace App.Core.Storage;

public interface INotesSearchService
{
    Task<IReadOnlyList<NotesSearchHit>> SearchAsync(
        string query,
        int max = 8,
        IReadOnlySet<string>? unlockedNotebookIds = null,
        CancellationToken ct = default);

    Task<NotesAskResult> AskAsync(
        string question,
        IReadOnlySet<string>? unlockedNotebookIds = null,
        CancellationToken ct = default);
}

public sealed record NotesSearchHit(
    string NotebookId,
    string NotebookTitle,
    string EntryId,
    string Snippet,
    float Score);

public sealed class NotesAskResult
{
    public string AnswerMarkdown { get; init; } = "";
    public IReadOnlyList<NotesSearchHit> Citations { get; init; } = Array.Empty<NotesSearchHit>();
    public string? Error { get; init; }
}
