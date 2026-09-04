namespace App.Core.Support;

public interface IContentReportService
{
    Task SubmitAsync(ContentReport report, CancellationToken ct = default);
}
