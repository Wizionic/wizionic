namespace App.Core.Help;

public interface IHelpCatalog
{
    IReadOnlyList<HelpTopic> Topics { get; }
    Task EnsureLoadedAsync(CancellationToken ct = default);
    HelpTopic? FindById(string? id);
    HelpTopic? FindByRoute(string path);
    Task<string> GetMarkdownAsync(string topicId, CancellationToken ct = default);
    IReadOnlyList<HelpTopic> Search(string query);
}
