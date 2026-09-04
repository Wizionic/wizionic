using System.Text;
using App.Core.Support;
using App.Core.Update;

namespace App.Shared.Services;

public sealed class ContentReportService : IContentReportService
{
    public const string ReportEmail = "daniellgoodwin@protonmail.com";
    public const string Subject = "Wizionic content report";

    private readonly IExternalUriOpener _opener;
    private readonly IUpdateService _updates;

    public ContentReportService(IExternalUriOpener opener, IUpdateService updates)
    {
        _opener = opener;
        _updates = updates;
    }

    public Task SubmitAsync(ContentReport report, CancellationToken ct = default)
    {
        var mailto = BuildMailto(report);
        return _opener.OpenAsync(mailto, ct);
    }

    internal string BuildMailto(ContentReport report)
    {
        var version = string.IsNullOrWhiteSpace(report.AppVersion)
            ? _updates.GetInstalledVersion()
            : report.AppVersion;
        if (string.IsNullOrWhiteSpace(version))
            version = "unknown";

        var model = string.IsNullOrWhiteSpace(report.ModelId)
            ? "unknown / local"
            : report.ModelId.Trim();

        var body = new StringBuilder();
        body.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        body.AppendLine($"App version: {version}");
        body.AppendLine($"Model: {model}");
        body.AppendLine($"Surface: {ContentReportSurfaces.ToWire(report.Surface)}");
        body.AppendLine();
        body.AppendLine("What happened:");
        body.AppendLine(report.WhatHappened.Trim());
        if (!string.IsNullOrWhiteSpace(report.ExtraDetail))
        {
            body.AppendLine();
            body.AppendLine("Extra detail:");
            body.AppendLine(report.ExtraDetail.Trim());
        }

        var subject = Uri.EscapeDataString(Subject);
        var encodedBody = Uri.EscapeDataString(body.ToString());
        return $"mailto:{ReportEmail}?subject={subject}&body={encodedBody}";
    }
}
