using App.Core.Connectors;

namespace App.Shared.Services.Connectors;

/// <summary>Static marketplace catalog for Featured OAuth + known MCP-style entries.</summary>
public static class ConnectorCatalog
{
    public static IReadOnlyList<ConnectorCatalogEntry> FeaturedOAuth { get; } =
    [
        new(
            Id: "gmail",
            DisplayName: "Gmail",
            Description: "Read and send email via Gmail API.",
            Kind: ConnectorKind.OpenApiOAuth,
            IconKey: "gmail",
            Featured: true,
            Scopes:
            [
                "gmail.readonly",
                "gmail.send",
                "gmail.modify"
            ],
            DocsUrl: "https://developers.google.com/gmail/api",
            OAuthProviderId: "google"),
        new(
            Id: "google-calendar",
            DisplayName: "Google Calendar",
            Description: "List and manage Google Calendar events.",
            Kind: ConnectorKind.OpenApiOAuth,
            IconKey: "google-calendar",
            Featured: true,
            Scopes: ["calendar", "calendar.events"],
            DocsUrl: "https://developers.google.com/calendar",
            OAuthProviderId: "google"),
        new(
            Id: "github",
            DisplayName: "GitHub",
            Description: "Repositories, issues, and pull requests.",
            Kind: ConnectorKind.OpenApiOAuth,
            IconKey: "github",
            Featured: true,
            Scopes: ["repo", "read:user"],
            DocsUrl: "https://docs.github.com/en/rest",
            OAuthProviderId: "github"),
        new(
            Id: "notion",
            DisplayName: "Notion",
            Description: "Search pages and query databases.",
            Kind: ConnectorKind.OpenApiOAuth,
            IconKey: "notion",
            Featured: true,
            DocsUrl: "https://developers.notion.com",
            OAuthProviderId: "notion"),
        new(
            Id: "stripe",
            DisplayName: "Stripe",
            Description: "List customers, products, and invoices.",
            Kind: ConnectorKind.OpenApiOAuth,
            IconKey: "stripe",
            Featured: true,
            DocsUrl: "https://stripe.com/docs/api",
            OAuthProviderId: "stripe"),
    ];

    public static ConnectorCatalogEntry? GetOAuth(string id) =>
        FeaturedOAuth.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static string DisplayNameFor(string connectorId) =>
        GetOAuth(connectorId)?.DisplayName
        ?? connectorId;
}
