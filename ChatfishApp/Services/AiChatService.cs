using System.Net.Http.Json;
using System.Text.Json;

namespace ChatfishApp.Services;

/// <summary>
/// Legacy single-provider service (hardcoded Groq). Kept for reference during multi-provider transition.
/// New code uses AiProviderService + IChatClient (Microsoft.Extensions.AI).
/// </summary>
[Obsolete("Use AiProviderService (IChatClient based) instead.")]
public class AiChatService
{
    private readonly HttpClient _http;

    public AiChatService(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> GetBotReply(string model, List<(string role, string content)> history)
    {
        var request = new
        {
            model = model,
            messages = history.Select(h => new { role = h.role, content = h.content }).ToArray()
        };

        var response = await _http.PostAsJsonAsync("chat/completions", request);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (json.TryGetProperty("error", out var err))
            return $"ChatFish error: {err.GetProperty("message").GetString()}";

        return json.GetProperty("choices")[0]
                   .GetProperty("message")
                   .GetProperty("content")
                   .GetString()
               ?? "I'm not sure how to respond.";
    }
}
