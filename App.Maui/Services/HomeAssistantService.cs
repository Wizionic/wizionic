using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using App.Core.SmartHome;
using App.Core.Storage;
using Microsoft.Extensions.Logging;

namespace App.Maui.Services;

/// <summary>
/// Direct LAN client for a local Home Assistant instance.
/// </summary>
public sealed class HomeAssistantService : ISmartHomeService
{
    /// <summary>
    /// Domains the assistant can typically control. Explicit domain filters can still list others (e.g. sensor).
    /// </summary>
    private static readonly HashSet<string> ControllableDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "light", "switch", "media_player", "climate", "cover", "fan", "lock",
        "scene", "script", "input_boolean", "input_button", "input_select", "input_number",
        "button", "remote", "vacuum", "humidifier", "water_heater",
        "alarm_control_panel", "siren", "valve", "lawn_mower", "number", "select", "todo"
    };

    private const int MaxCatalogChars = 10_000;
    private const int MaxListLines = 200;
    private const int MaxEntitiesPerDomainInCatalog = 40;
    private const string NotConfigured =
        "Home Assistant is not configured. Add base URL and token on the Home Assistant page.";

    private static readonly HashSet<string> ToggleDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "light", "switch", "fan", "input_boolean", "cover", "lock", "remote", "siren", "humidifier"
    };

    private const string AreasTemplate =
        "{% for a in areas() %}{{ a }}|{{ area_name(a) }}|{% for e in area_entities(a) %}{{ e }},{% endfor %}\n{% endfor %}";

    private readonly IKeyStore _keyStore;
    private readonly ILogger<HomeAssistantService> _logger;
    private readonly HttpClient _http;

    public HomeAssistantService(IKeyStore keyStore, ILogger<HomeAssistantService> logger)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = CreateHttpClient();
    }

    public bool IsConfigured =>
        HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out _, out _);

    public async Task<string> TestConnectionAsync(string baseUrl, string token, CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(baseUrl, token, out var url, out var normalizedToken))
            return "Enter both base URL and access token.";

        Log($"TestConnection starting → {DescribeEndpoint(url)}/api/ (token length {normalizedToken.Length})");

        var apiResult = await SendAsync(HttpMethod.Get, $"{url}/api/", normalizedToken, content: null, ct, "TestConnection/api");
        if (IsHaFailure(apiResult))
            return apiResult;

        Log("TestConnection /api/ OK — probing sun.sun entity state");
        var stateResult = await SendAsync(
            HttpMethod.Get,
            $"{url}/api/states/sun.sun",
            normalizedToken,
            content: null,
            ct,
            "TestConnection/sun.sun");

        if (IsHaFailure(stateResult))
            return stateResult;

        Log("TestConnection completed successfully");
        return stateResult;
    }

    public async Task<string> CallServiceAsync(
        string domain,
        string service,
        object serviceData,
        CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return NotConfigured;

        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(service))
            return "Domain and service are required (e.g. media_player / media_play).";

        var endpoint = $"{url}/api/services/{Uri.EscapeDataString(domain.Trim())}/{Uri.EscapeDataString(service.Trim())}";
        var raw = await SendAsync(HttpMethod.Post, endpoint, token, JsonContent.Create(serviceData), ct, $"CallService/{domain}.{service}");
        if (IsHaFailure(raw))
            return raw;

        return FormatServiceSuccess(domain.Trim(), service.Trim(), raw);
    }

    public Task<string> GetEntityStateAsync(string entityId, CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return Task.FromResult(NotConfigured);

        return GetEntityStateAsync(url, token, entityId, ct);
    }

    public Task<string> ListLightEntitiesAsync(CancellationToken ct = default) =>
        ListEntitiesAsync(domain: "light", search: null, ct);

    public Task<string> ListEntitiesAsync(string? domain = null, string? search = null, CancellationToken ct = default) =>
        ListEntitiesCoreAsync(domain, search, ct);

    public async Task<HaInstanceInfo> GetInstanceInfoAsync(CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return new HaInstanceInfo(false, null, null, null, NotConfigured);

        var raw = await SendAsync(HttpMethod.Get, $"{url}/api/config", token, content: null, ct, "GetConfig");
        if (IsHaFailure(raw))
            return new HaInstanceInfo(false, null, null, null, raw);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var location = root.TryGetProperty("location_name", out var loc) ? loc.GetString() : null;
            var version = root.TryGetProperty("version", out var ver) ? ver.GetString() : null;
            var tz = root.TryGetProperty("time_zone", out var tzEl) ? tzEl.GetString() : null;
            return new HaInstanceInfo(true, location, version, tz, null);
        }
        catch (Exception ex)
        {
            return new HaInstanceInfo(false, null, null, null, $"Could not parse Home Assistant config: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<HaDeviceRow>> GetDeviceRowsAsync(CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return [];

        var (ok, _, entities) = await LoadEntitiesAsync(url, token, ct, "GetDeviceRows");
        if (!ok)
            return [];

        var areas = await LoadAreasAsync(url, token, ct);
        ApplyAreas(entities, areas);

        return entities
            .Where(e => ControllableDomains.Contains(DomainOf(e.EntityId)))
            .OrderBy(e => e.AreaName ?? "zzz", StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => DomainOf(e.EntityId), StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new HaDeviceRow(
                e.EntityId,
                e.FriendlyName,
                DomainOf(e.EntityId),
                e.State,
                e.AreaName,
                ToggleDomains.Contains(DomainOf(e.EntityId))))
            .ToList();
    }

    public async Task<string> BuildDeviceCatalogAsync(CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return NotConfigured;

        var (ok, error, entities) = await LoadEntitiesAsync(url, token, ct, "BuildDeviceCatalog");
        if (!ok)
            return error ?? "Could not load Home Assistant entities.";

        var areas = await LoadAreasAsync(url, token, ct);
        ApplyAreas(entities, areas);

        var controllable = entities.Where(e => ControllableDomains.Contains(DomainOf(e.EntityId))).ToList();
        if (controllable.Count == 0)
            return "No controllable entities found in Home Assistant.";

        var byDomain = controllable
            .GroupBy(e => DomainOf(e.EntityId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Home Assistant controllable devices ({controllable.Count} entities, {byDomain.Count} domains):");
        sb.AppendLine("Use ListEntities(domain, search) for full/filtered lists. Search matches friendly name, entity_id, and area.");

        if (areas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Areas ({areas.Count}):");
            foreach (var area in areas.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Take(40))
            {
                var names = area.EntityIds
                    .Select(id => controllable.FirstOrDefault(e => e.EntityId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    .Where(e => e is not null)
                    .Take(8)
                    .Select(e => e!.FriendlyName);
                var listed = string.Join(", ", names);
                sb.AppendLine(string.IsNullOrWhiteSpace(listed)
                    ? $"  • {area.Name}"
                    : $"  • {area.Name} → {listed}");
            }
        }

        foreach (var group in byDomain)
        {
            var list = group.OrderBy(e => e.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList();
            sb.AppendLine();
            sb.AppendLine($"{group.Key} ({list.Count}):");
            var take = Math.Min(list.Count, MaxEntitiesPerDomainInCatalog);
            for (var i = 0; i < take; i++)
            {
                var e = list[i];
                var areaBit = string.IsNullOrWhiteSpace(e.AreaName) ? "" : $" [{e.AreaName}]";
                sb.AppendLine($"  • {e.FriendlyName}{areaBit} → {e.EntityId} ({e.State})");
            }

            if (list.Count > take)
                sb.AppendLine($"  … and {list.Count - take} more (call ListEntities domain=\"{group.Key}\")");
        }

        var catalog = sb.ToString().TrimEnd();
        if (catalog.Length <= MaxCatalogChars)
            return catalog;

        var compact = new StringBuilder();
        compact.AppendLine($"Home Assistant controllable devices ({controllable.Count} entities, {byDomain.Count} domains) — catalog truncated for size.");
        compact.AppendLine("Call ListEntities with domain and/or search (including area names) to resolve devices.");
        if (areas.Count > 0)
            compact.AppendLine($"Areas: {string.Join(", ", areas.Select(a => a.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(24))}");
        foreach (var group in byDomain)
            compact.AppendLine($"  • {group.Key}: {group.Count()} entity(ies)");
        return compact.ToString().TrimEnd();
    }

    public async Task<string> ListServicesAsync(string? domain = null, CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return NotConfigured;

        var json = await SendAsync(HttpMethod.Get, $"{url}/api/services", token, content: null, ct, "ListServices");
        if (IsHaFailure(json))
            return json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            var domainFilter = domain?.Trim();
            var matchCount = 0;

            foreach (var domainObj in doc.RootElement.EnumerateArray())
            {
                if (!domainObj.TryGetProperty("domain", out var domainEl))
                    continue;

                var domainName = domainEl.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(domainFilter) &&
                    !domainName.Equals(domainFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!domainObj.TryGetProperty("services", out var servicesEl))
                    continue;

                var serviceNames = new List<string>();
                if (servicesEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in servicesEl.EnumerateObject())
                        serviceNames.Add(prop.Name);
                }
                else if (servicesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in servicesEl.EnumerateArray())
                    {
                        if (s.ValueKind == JsonValueKind.String)
                            serviceNames.Add(s.GetString() ?? "");
                    }
                }

                serviceNames.Sort(StringComparer.OrdinalIgnoreCase);
                if (serviceNames.Count == 0)
                    continue;

                matchCount++;
                sb.AppendLine($"{domainName}: {string.Join(", ", serviceNames)}");
            }

            if (matchCount == 0)
            {
                return string.IsNullOrWhiteSpace(domainFilter)
                    ? "No services returned by Home Assistant."
                    : $"No services found for domain '{domainFilter}'.";
            }

            var header = string.IsNullOrWhiteSpace(domainFilter)
                ? $"Available services ({matchCount} domains):"
                : $"Services for domain '{domainFilter}':";

            return $"{header}\n{sb.ToString().TrimEnd()}";
        }
        catch (Exception ex)
        {
            return $"Could not parse Home Assistant services: {ex.Message}";
        }
    }

    public async Task<string> ProcessConversationAsync(string text, string? conversationId = null, CancellationToken ct = default)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return NotConfigured;

        if (string.IsNullOrWhiteSpace(text))
            return "Conversation text is required.";

        var payload = new Dictionary<string, object?>
        {
            ["text"] = text.Trim(),
            ["language"] = "en"
        };
        if (!string.IsNullOrWhiteSpace(conversationId))
            payload["conversation_id"] = conversationId.Trim();

        var raw = await SendAsync(
            HttpMethod.Post,
            $"{url}/api/conversation/process",
            token,
            JsonContent.Create(payload),
            ct,
            "ProcessConversation");

        if (IsHaFailure(raw))
            return raw;

        return FormatConversationResponse(raw);
    }

    // ── Entity fetch / format helpers ──────────────────────────────────────

    private async Task<string> FetchStatesJsonAsync(string url, string token, CancellationToken ct, string operation)
    {
        return await SendAsync(HttpMethod.Get, $"{url}/api/states", token, content: null, ct, operation);
    }

    private async Task<List<HaAreaInfo>> LoadAreasAsync(string url, string token, CancellationToken ct)
    {
        try
        {
            var raw = await SendAsync(
                HttpMethod.Post,
                $"{url}/api/template",
                token,
                JsonContent.Create(new { template = AreasTemplate }),
                ct,
                "ListAreas");
            if (IsHaFailure(raw) || string.IsNullOrWhiteSpace(raw))
                return [];

            var areas = new List<HaAreaInfo>();
            foreach (var line in raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('|', 3);
                if (parts.Length < 2)
                    continue;
                var id = parts[0].Trim();
                var name = parts[1].Trim();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    continue;
                var entityIds = parts.Length < 3
                    ? Array.Empty<string>()
                    : parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                areas.Add(new HaAreaInfo(id, name, entityIds));
            }

            return areas;
        }
        catch
        {
            return [];
        }
    }

    private static void ApplyAreas(List<EntitySnapshot> entities, List<HaAreaInfo> areas)
    {
        if (areas.Count == 0)
            return;

        var byEntity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in areas)
        {
            foreach (var id in area.EntityIds)
                byEntity[id] = area.Name;
        }

        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (!byEntity.TryGetValue(e.EntityId, out var areaName))
                continue;
            var extra = string.IsNullOrWhiteSpace(e.ExtraSearchText)
                ? areaName
                : $"{e.ExtraSearchText} {areaName}";
            entities[i] = e with { ExtraSearchText = extra, AreaName = areaName };
        }
    }

    private async Task<(bool Ok, string? Error, List<EntitySnapshot> Entities)> LoadEntitiesAsync(
        string url, string token, CancellationToken ct, string operation)
    {
        var json = await FetchStatesJsonAsync(url, token, ct, operation);
        if (IsHaFailure(json))
            return (false, json, []);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<EntitySnapshot>();
            foreach (var entity in doc.RootElement.EnumerateArray())
            {
                if (TryParseEntity(entity, out var snap))
                    list.Add(snap);
            }

            return (true, null, list);
        }
        catch (Exception ex)
        {
            return (false, $"Could not parse Home Assistant states: {ex.Message}", []);
        }
    }

    private async Task<string> ListEntitiesCoreAsync(string? domain, string? search, CancellationToken ct)
    {
        if (!HomeAssistantCredentials.TryNormalize(_keyStore.HomeAssistantBaseUrl, _keyStore.HomeAssistantToken, out var url, out var token))
            return NotConfigured;

        var (ok, error, entities) = await LoadEntitiesAsync(url, token, ct, "ListEntities");
        if (!ok)
            return error ?? "Could not load Home Assistant entities.";

        var areas = await LoadAreasAsync(url, token, ct);
        ApplyAreas(entities, areas);

        var filtered = FilterEntities(entities, domain, search, controllableOnly: string.IsNullOrWhiteSpace(domain));
        if (filtered.Count == 0)
        {
            var scope = string.IsNullOrWhiteSpace(domain) ? "controllable domains" : $"domain '{domain.Trim()}'";
            var searchPart = string.IsNullOrWhiteSpace(search) ? "" : $" matching '{search.Trim()}'";
            return $"No entities found in {scope}{searchPart}. Try a different domain or search term, or ListEntities without filters.";
        }

        return FormatEntityList(filtered, domain, search);
    }

    // Override the public method body to use the correct path — rewrite public method below via replacing whole file cleanly

    private static List<EntitySnapshot> FilterEntities(
        List<EntitySnapshot> entities,
        string? domain,
        string? search,
        bool controllableOnly)
    {
        IEnumerable<EntitySnapshot> q = entities;

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var d = domain.Trim();
            q = q.Where(e => DomainOf(e.EntityId).Equals(d, StringComparison.OrdinalIgnoreCase));
        }
        else if (controllableOnly)
        {
            q = q.Where(e => ControllableDomains.Contains(DomainOf(e.EntityId)));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var terms = search.Trim().Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            q = q.Where(e => terms.All(term =>
                e.EntityId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.FriendlyName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (e.AreaName is not null && e.AreaName.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (e.ExtraSearchText is not null && e.ExtraSearchText.Contains(term, StringComparison.OrdinalIgnoreCase))));
        }

        var list = q.ToList();
        list.Sort((a, b) =>
        {
            var byName = string.Compare(a.FriendlyName, b.FriendlyName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(a.EntityId, b.EntityId, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    private static string FormatEntityList(List<EntitySnapshot> entities, string? domain, string? search)
    {
        var sb = new StringBuilder();
        var scope = string.IsNullOrWhiteSpace(domain) ? "controllable" : domain.Trim();
        var searchPart = string.IsNullOrWhiteSpace(search) ? "" : $", search=\"{search.Trim()}\"";
        var total = entities.Count;
        var shown = Math.Min(total, MaxListLines);

        sb.AppendLine($"Found {total} {scope} entity(ies){searchPart}:");

        // Group by domain for readability when multi-domain
        var grouped = entities.Take(shown).GroupBy(e => DomainOf(e.EntityId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            if (string.IsNullOrWhiteSpace(domain))
                sb.AppendLine($"[{group.Key}]");
            foreach (var e in group)
            {
                var areaBit = string.IsNullOrWhiteSpace(e.AreaName) ? "" : $" [{e.AreaName}]";
                sb.AppendLine($"  • {e.FriendlyName}{areaBit} → {e.EntityId} (currently {e.State})");
            }
        }

        if (total > shown)
            sb.AppendLine($"… and {total - shown} more. Narrow with domain or search.");

        return sb.ToString().TrimEnd();
    }

    private static bool TryParseEntity(JsonElement entity, out EntitySnapshot snap)
    {
        snap = default!;
        if (!entity.TryGetProperty("entity_id", out var idEl))
            return false;

        var entityId = idEl.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(entityId))
            return false;

        var state = entity.TryGetProperty("state", out var stateEl) ? stateEl.GetString() ?? "unknown" : "unknown";
        var friendly = entityId;
        string? extra = null;

        if (entity.TryGetProperty("attributes", out var attrs))
        {
            if (attrs.TryGetProperty("friendly_name", out var fn))
                friendly = fn.GetString() ?? entityId;

            // Help media_player search by source names
            if (attrs.TryGetProperty("source_list", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var s in sources.EnumerateArray())
                {
                    if (s.ValueKind == JsonValueKind.String && s.GetString() is { } v)
                        parts.Add(v);
                }

                if (parts.Count > 0)
                    extra = string.Join(' ', parts);
            }

            if (attrs.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String)
                extra = string.IsNullOrEmpty(extra) ? source.GetString() : $"{extra} {source.GetString()}";
        }

        snap = new EntitySnapshot(entityId, friendly, state, extra);
        return true;
    }

    private static string DomainOf(string entityId)
    {
        var dot = entityId.IndexOf('.');
        return dot > 0 ? entityId[..dot] : entityId;
    }

    private static string FormatServiceSuccess(string domain, string service, string body)
    {
        var preview = SummarizeChangedStates(body);
        if (string.IsNullOrWhiteSpace(preview))
            return $"Called {domain}.{service} successfully.";

        return $"Called {domain}.{service} successfully.\n{preview}";
    }

    private static string SummarizeChangedStates(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body is "null" or "[]" or "{}")
            return "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Newer HA with return_response shape, or plain array of states
            JsonElement states = root;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("changed_states", out var changed))
                states = changed;

            if (states.ValueKind != JsonValueKind.Array)
                return Truncate(body, 400);

            var lines = new List<string>();
            foreach (var item in states.EnumerateArray())
            {
                if (!item.TryGetProperty("entity_id", out var idEl))
                    continue;

                var id = idEl.GetString() ?? "";
                var state = item.TryGetProperty("state", out var st) ? st.GetString() ?? "?" : "?";
                var name = id;
                if (item.TryGetProperty("attributes", out var attrs) &&
                    attrs.TryGetProperty("friendly_name", out var fn))
                    name = fn.GetString() ?? id;

                lines.Add($"  • {name} ({id}) → {state}");
                if (lines.Count >= 8)
                    break;
            }

            if (lines.Count == 0)
                return "";

            return "Changed states:\n" + string.Join('\n', lines);
        }
        catch
        {
            return Truncate(body, 400);
        }
    }

    private static string FormatConversationResponse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var sb = new StringBuilder();

            string? conversationId = null;
            if (root.TryGetProperty("conversation_id", out var cid))
                conversationId = cid.GetString();

            if (root.TryGetProperty("response", out var response))
            {
                var responseType = response.TryGetProperty("response_type", out var rt) ? rt.GetString() : null;
                string? speech = null;
                if (response.TryGetProperty("speech", out var speechObj))
                {
                    if (speechObj.TryGetProperty("plain", out var plain) &&
                        plain.TryGetProperty("speech", out var plainText))
                        speech = plainText.GetString();
                    else if (speechObj.TryGetProperty("ssml", out var ssml) &&
                             ssml.TryGetProperty("speech", out var ssmlText))
                        speech = ssmlText.GetString();
                }

                if (string.Equals(responseType, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = "unknown";
                    if (response.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("code", out var codeEl))
                        code = codeEl.GetString() ?? code;

                    sb.AppendLine($"Assist could not handle this (code: {code}).");
                    if (!string.IsNullOrWhiteSpace(speech))
                        sb.AppendLine(speech);
                    sb.AppendLine("Fall back to ListEntities + CallService (or ControlLight) for precise control.");
                }
                else
                {
                    sb.AppendLine($"Assist result ({responseType ?? "ok"}):");
                    if (!string.IsNullOrWhiteSpace(speech))
                        sb.AppendLine(speech);

                    if (response.TryGetProperty("data", out var data) &&
                        data.TryGetProperty("success", out var success) &&
                        success.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in success.EnumerateArray())
                        {
                            var name = t.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var type = t.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                            var id = t.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(id))
                                sb.AppendLine($"  • {name ?? id} ({type}{(id is null ? "" : $", {id}")})");
                        }
                    }
                }
            }
            else
            {
                sb.AppendLine(Truncate(raw, 600));
            }

            if (!string.IsNullOrWhiteSpace(conversationId))
                sb.AppendLine($"conversation_id: {conversationId}");

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return Truncate(raw, 600);
        }
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;
        return text[..max] + "…";
    }

    public static bool IsHaFailure(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return true;

        return result.StartsWith("HA error", StringComparison.OrdinalIgnoreCase) ||
               result.StartsWith("Connection", StringComparison.OrdinalIgnoreCase) ||
               result.StartsWith("Home Assistant is not configured", StringComparison.OrdinalIgnoreCase) ||
               result.StartsWith("Enter both", StringComparison.OrdinalIgnoreCase) ||
               result.StartsWith("Domain and service", StringComparison.OrdinalIgnoreCase) ||
               result.StartsWith("Could not parse", StringComparison.OrdinalIgnoreCase) ||
               result.StartsWith("Smart home integration", StringComparison.OrdinalIgnoreCase);
    }

    private Task<string> GetEntityStateAsync(
        string baseUrl,
        string token,
        string entityId,
        CancellationToken ct)
    {
        if (!HomeAssistantCredentials.TryNormalize(baseUrl, token, out var url, out var normalizedToken))
            return Task.FromResult("Enter both base URL and access token.");

        if (string.IsNullOrWhiteSpace(entityId))
            return Task.FromResult("Entity ID is required.");

        var endpoint = $"{url}/api/states/{Uri.EscapeDataString(entityId.Trim())}";
        return SendAsync(HttpMethod.Get, endpoint, normalizedToken, content: null, ct, $"GetEntityState/{entityId}");
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string url,
        string token,
        HttpContent? content,
        CancellationToken ct,
        string operation)
    {
        var sw = Stopwatch.StartNew();
        Log($"HTTP {method} {DescribeEndpoint(url)} [{operation}]");

        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await _http.SendAsync(request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();

            Log($"HTTP {method} {DescribeEndpoint(url)} → {(int)resp.StatusCode} {resp.StatusCode} in {sw.ElapsedMilliseconds}ms, body {body.Length} chars [{operation}]");

            if (resp.IsSuccessStatusCode)
                return body;

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                return $"HA error Unauthorized: token rejected (length {token.Length} chars). " +
                       "Re-paste the long-lived token from HA Profile → Security, then test again. " +
                       "Note: MAUI talks to Home Assistant directly — these calls won't appear in browser DevTools.";
            }

            return $"HA error {resp.StatusCode}: {body}";
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"HTTP {method} {DescribeEndpoint(url)} FAILED after {sw.ElapsedMilliseconds}ms [{operation}]: {DescribeException(ex)}");
            return FormatNetworkError(ex, url, sw.Elapsed, operation, ct);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            // LAN IPs must not go through the system proxy (common cause of 15s+ hangs).
            UseProxy = false,
            Proxy = null,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    private string FormatNetworkError(Exception ex, string url, TimeSpan elapsed, string operation, CancellationToken ct)
    {
        var endpoint = DescribeEndpoint(url);
        var root = ex.InnerException ?? ex;

        if (ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"Connection timed out after {elapsed.TotalSeconds:0.#}s reaching {endpoint} [{operation}]. " +
                   "Checks: Home Assistant is running, the IP/port is correct, this PC is on the same LAN, " +
                   "and Windows Firewall allows Wizionic on Private networks. " +
                   $"Details: {DescribeException(root)}";
        }

        if (root is HttpRequestException or SocketException)
        {
            return $"Connection failed to {endpoint} after {elapsed.TotalSeconds:0.#}s [{operation}]. " +
                   $"Details: {DescribeException(root)}";
        }

        return $"Connection failed to {endpoint} [{operation}]: {DescribeException(ex)}";
    }

    private void Log(string message)
    {
        _logger.LogInformation("{Message}", message);
        Console.WriteLine($"[HomeAssistant] {message}");
        Debug.WriteLine($"[HomeAssistant] {message}");
    }

    private static string DescribeEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
    }

    private static string DescribeException(Exception ex)
    {
        var parts = new List<string> { $"{ex.GetType().Name}: {ex.Message}" };
        if (ex.InnerException != null)
            parts.Add($"inner {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        return string.Join(" | ", parts);
    }

    private sealed record EntitySnapshot(
        string EntityId,
        string FriendlyName,
        string State,
        string? ExtraSearchText,
        string? AreaName = null);
}
