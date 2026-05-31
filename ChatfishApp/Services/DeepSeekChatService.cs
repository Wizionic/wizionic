using System.Text.Json;

namespace ChatfishApp.Services;

public class DeepSeekChatService
{
    private readonly HttpClient _http;

    public DeepSeekChatService(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> GetBotReply(string userMessage)
    {
        // Replace with your OpenAI or Azure OpenAI endpoint
        var request = new
        {
            model = "deepseek-chat",
            messages = new[]
            {
                new { role = "system", content = "You are SageBot, a friendly assistant." },
                new { role = "user", content = userMessage }
            }
        };

        var response = await _http.PostAsJsonAsync("/chat/completions", request);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        // If there's an error, return it instead of crashing
        if (json.TryGetProperty("error", out var err))
        {
            return $"SageBot error: {err.GetProperty("message").GetString()}";
        }

        // Extract the assistant message safely
        var content =
            json.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

        return content ?? "I'm not sure how to respond.";
    }
}
