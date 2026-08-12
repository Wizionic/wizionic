using App.Core.Connectors;

namespace App.Shared.Services.Connectors;

/// <summary>
/// Hand-curated OpenAPI-shaped operations (≤8 per connector) exposed as AI tools.
/// </summary>
public static class CuratedConnectorSpecs
{
    private static readonly Dictionary<string, CuratedConnectorSpec> Specs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gmail"] = Gmail(),
            ["google-calendar"] = GoogleCalendar(),
            ["github"] = GitHub(),
            ["notion"] = Notion(),
            ["stripe"] = Stripe()
        };

    public static CuratedConnectorSpec? Get(string connectorId) =>
        Specs.GetValueOrDefault(connectorId);

    public static IEnumerable<string> AllConnectorIds => Specs.Keys;

    private static CuratedConnectorSpec Gmail() => new()
    {
        ConnectorId = "gmail",
        Operations =
        [
            new CuratedConnectorOperation
            {
                Name = "gmail_list_messages",
                Description = "List Gmail message ids matching an optional query (Gmail search syntax, e.g. is:unread).",
                Method = "GET",
                UrlTemplate = "https://gmail.googleapis.com/gmail/v1/users/me/messages",
                Parameters =
                [
                    new() { Name = "q", In = "query", Required = false, Description = "Gmail search query" },
                    new() { Name = "maxResults", In = "query", Required = false, Description = "Max results (1-50)", Type = "integer" }
                ]
            },
            new CuratedConnectorOperation
            {
                Name = "gmail_get_message",
                Description = "Get a Gmail message by id (metadata + snippet / body parts).",
                Method = "GET",
                UrlTemplate = "https://gmail.googleapis.com/gmail/v1/users/me/messages/{id}",
                Parameters =
                [
                    new() { Name = "id", In = "path", Required = true, Description = "Message id" },
                    new() { Name = "format", In = "query", Required = false, Description = "full | metadata | minimal | raw" }
                ]
            },
            new CuratedConnectorOperation
            {
                Name = "gmail_list_labels",
                Description = "List Gmail labels for the user.",
                Method = "GET",
                UrlTemplate = "https://gmail.googleapis.com/gmail/v1/users/me/labels"
            },
            new CuratedConnectorOperation
            {
                Name = "gmail_send_message",
                Description = "Send an email via Gmail. Provide raw RFC 2822 message base64url-encoded in JSON body {\"raw\":\"...\"}. Prefer simple to/subject/body helpers when available; for this tool pass body_json with a raw field.",
                Method = "POST",
                UrlTemplate = "https://gmail.googleapis.com/gmail/v1/users/me/messages/send",
                RequestBodyJson = true,
                RequestBodyDescription = "JSON: { \"raw\": \"<base64url RFC2822>\" }"
            }
        ]
    };

    private static CuratedConnectorSpec GoogleCalendar() => new()
    {
        ConnectorId = "google-calendar",
        Operations =
        [
            new CuratedConnectorOperation
            {
                Name = "gcal_list_calendars",
                Description = "List the user's Google calendars.",
                Method = "GET",
                UrlTemplate = "https://www.googleapis.com/calendar/v3/users/me/calendarList"
            },
            new CuratedConnectorOperation
            {
                Name = "gcal_list_events",
                Description = "List events on a calendar in a time range (ISO 8601 timeMin/timeMax).",
                Method = "GET",
                UrlTemplate = "https://www.googleapis.com/calendar/v3/calendars/{calendarId}/events",
                Parameters =
                [
                    new() { Name = "calendarId", In = "path", Required = true, Description = "Calendar id (use 'primary' for main)" },
                    new() { Name = "timeMin", In = "query", Required = false, Description = "RFC3339 lower bound" },
                    new() { Name = "timeMax", In = "query", Required = false, Description = "RFC3339 upper bound" },
                    new() { Name = "maxResults", In = "query", Required = false, Type = "integer" },
                    new() { Name = "singleEvents", In = "query", Required = false, Description = "true to expand recurrences" },
                    new() { Name = "orderBy", In = "query", Required = false, Description = "startTime or updated" }
                ]
            },
            new CuratedConnectorOperation
            {
                Name = "gcal_create_event",
                Description = "Create a Google Calendar event. body_json is the Events resource (summary, start, end, location, description).",
                Method = "POST",
                UrlTemplate = "https://www.googleapis.com/calendar/v3/calendars/{calendarId}/events",
                Parameters =
                [
                    new() { Name = "calendarId", In = "path", Required = true, Description = "Calendar id (primary)" }
                ],
                RequestBodyJson = true,
                RequestBodyDescription = "Event JSON with summary, start.dateTime, end.dateTime, timeZone, etc."
            },
            new CuratedConnectorOperation
            {
                Name = "gcal_update_event",
                Description = "Patch an existing Google Calendar event.",
                Method = "PATCH",
                UrlTemplate = "https://www.googleapis.com/calendar/v3/calendars/{calendarId}/events/{eventId}",
                Parameters =
                [
                    new() { Name = "calendarId", In = "path", Required = true },
                    new() { Name = "eventId", In = "path", Required = true }
                ],
                RequestBodyJson = true,
                RequestBodyDescription = "Partial Event JSON fields to update"
            }
        ]
    };

    private static CuratedConnectorSpec GitHub() => new()
    {
        ConnectorId = "github",
        Operations =
        [
            new CuratedConnectorOperation
            {
                Name = "github_list_repos",
                Description = "List repositories for the authenticated user.",
                Method = "GET",
                UrlTemplate = "https://api.github.com/user/repos",
                Parameters =
                [
                    new() { Name = "per_page", In = "query", Required = false, Type = "integer" },
                    new() { Name = "sort", In = "query", Required = false, Description = "created|updated|pushed|full_name" }
                ]
            },
            new CuratedConnectorOperation
            {
                Name = "github_list_issues",
                Description = "List issues for a repository.",
                Method = "GET",
                UrlTemplate = "https://api.github.com/repos/{owner}/{repo}/issues",
                Parameters =
                [
                    new() { Name = "owner", In = "path", Required = true },
                    new() { Name = "repo", In = "path", Required = true },
                    new() { Name = "state", In = "query", Required = false, Description = "open|closed|all" },
                    new() { Name = "per_page", In = "query", Required = false, Type = "integer" }
                ]
            },
            new CuratedConnectorOperation
            {
                Name = "github_create_issue",
                Description = "Create an issue. body_json: {\"title\":\"...\",\"body\":\"...\"}.",
                Method = "POST",
                UrlTemplate = "https://api.github.com/repos/{owner}/{repo}/issues",
                Parameters =
                [
                    new() { Name = "owner", In = "path", Required = true },
                    new() { Name = "repo", In = "path", Required = true }
                ],
                RequestBodyJson = true,
                RequestBodyDescription = "Issue JSON with title and optional body/labels"
            },
            new CuratedConnectorOperation
            {
                Name = "github_get_user",
                Description = "Get the authenticated GitHub user profile.",
                Method = "GET",
                UrlTemplate = "https://api.github.com/user"
            }
        ]
    };

    private static CuratedConnectorSpec Notion() => new()
    {
        ConnectorId = "notion",
        Operations =
        [
            new CuratedConnectorOperation
            {
                Name = "notion_search",
                Description = "Search Notion pages and databases. body_json optional {\"query\":\"...\"}.",
                Method = "POST",
                UrlTemplate = "https://api.notion.com/v1/search",
                RequestBodyJson = true,
                RequestBodyDescription = "Search body, e.g. {\"query\":\"notes\"}"
            },
            new CuratedConnectorOperation
            {
                Name = "notion_get_page",
                Description = "Retrieve a Notion page by id.",
                Method = "GET",
                UrlTemplate = "https://api.notion.com/v1/pages/{page_id}",
                Parameters =
                [
                    new() { Name = "page_id", In = "path", Required = true }
                ]
            },
            new CuratedConnectorOperation
            {
                Name = "notion_query_database",
                Description = "Query a Notion database. body_json optional filter/sorts.",
                Method = "POST",
                UrlTemplate = "https://api.notion.com/v1/databases/{database_id}/query",
                Parameters =
                [
                    new() { Name = "database_id", In = "path", Required = true }
                ],
                RequestBodyJson = true,
                RequestBodyDescription = "Query body (filter, sorts, page_size)"
            }
        ]
    };

    private static CuratedConnectorSpec Stripe() => new()
    {
        ConnectorId = "stripe",
        Operations =
        [
            new CuratedConnectorOperation
            {
                Name = "stripe_list_customers",
                Description = "List Stripe customers.",
                Method = "GET",
                UrlTemplate = "https://api.stripe.com/v1/customers",
                Parameters =
                [
                    new() { Name = "limit", In = "query", Required = false, Type = "integer" }
                ]
            },
            new CuratedConnectorOperation
            {
                Name = "stripe_list_products",
                Description = "List Stripe products.",
                Method = "GET",
                UrlTemplate = "https://api.stripe.com/v1/products",
                Parameters =
                [
                    new() { Name = "limit", In = "query", Required = false, Type = "integer" }
                ]
            },
            new CuratedConnectorOperation
            {
                Name = "stripe_list_invoices",
                Description = "List Stripe invoices.",
                Method = "GET",
                UrlTemplate = "https://api.stripe.com/v1/invoices",
                Parameters =
                [
                    new() { Name = "limit", In = "query", Required = false, Type = "integer" },
                    new() { Name = "customer", In = "query", Required = false }
                ]
            }
        ]
    };
}
