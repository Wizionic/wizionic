namespace App.Core.Help;

public interface IHelpAskService
{
    Task<HelpAskResult> AskAsync(string question, CancellationToken ct = default);
    Task<HelpIndexStatus> GetIndexStatusAsync(CancellationToken ct = default);
    Task RebuildIndexAsync(CancellationToken ct = default);
}
