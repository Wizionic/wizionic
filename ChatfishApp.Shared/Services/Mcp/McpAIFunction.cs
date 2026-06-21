using System.Text.Json;
using Microsoft.Extensions.AI;
using ChatfishApp.Shared.Services.Tools;

namespace ChatfishApp.Shared.Services.Mcp;

/// <summary>
/// An AITool / AIFunction implementation that represents a single tool exposed by a remote MCP server.
/// When the model decides to call it (via UseFunctionInvocation middleware), InvokeAsync performs
/// the tools/call round-trip using the McpRemoteClient and returns the textual result.
/// 
/// Traces are recorded so the chat UI can show "🔌 server calling foo..." steps.
/// </summary>
public sealed class McpAIFunction : AIFunction
{
    private readonly McpRemoteClient _client;
    private readonly string _toolName;
    private readonly string _description;
    private readonly JsonElement? _inputSchema;
    private readonly string _serverDisplay; // short label for traces, e.g. "ac.tandem/docs-mcp"

    public McpAIFunction(
        McpRemoteClient client,
        string toolName,
        string description,
        JsonElement? inputSchema,
        string serverDisplay)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _toolName = toolName;
        _description = description;
        _inputSchema = inputSchema;
        _serverDisplay = string.IsNullOrWhiteSpace(serverDisplay) ? "mcp" : serverDisplay;
    }

    public override string Name => _toolName;

    public override string Description => string.IsNullOrWhiteSpace(_description)
        ? $"Remote MCP tool {_toolName} on {_serverDisplay}"
        : _description;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
        new Dictionary<string, object?> { ["mcpServer"] = true };

    /// <summary>
    /// We expose the MCP tool's declared inputSchema when available. This helps the model
    /// understand the expected parameters. If absent we return a permissive object schema.
    /// </summary>
    public override JsonElement JsonSchema
    {
        get
        {
            if (_inputSchema.HasValue && _inputSchema.Value.ValueKind == JsonValueKind.Object)
            {
                // Return a clone so callers can safely use it
                return JsonDocument.Parse(_inputSchema.Value.GetRawText()).RootElement;
            }

            // Fallback: permissive object (model will still be guided by the tool description + name)
            var fallback = JsonDocument.Parse("""{"type":"object","additionalProperties":true,"properties":{}}""");
            return fallback.RootElement;
        }
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken = default)
    {
        // Convert the arguments supplied by the ME.AI function-calling layer into a plain dict
        // that we can forward to the MCP "tools/call" as the "arguments" object.
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (arguments != null)
        {
            foreach (var kv in arguments)
            {
                // AIFunctionArguments values can be JsonElement, string, numbers, etc.
                // We pass them through as-is; System.Text.Json will serialize correctly on the wire.
                args[kv.Key] = kv.Value;
            }
        }

        var shortArgs = args.Count == 0 ? "" : " " + JsonSerializer.Serialize(args);
        ToolExecutionTrace.Record($"🔌 {_serverDisplay} → {_toolName}{shortArgs}");

        try
        {
            var resultText = await _client.CallToolAsync(_toolName, args, cancellationToken);

            var preview = resultText.Length > 220 ? resultText.Substring(0, 200) + "..." : resultText;
            ToolExecutionTrace.Record($"   ✅ {_toolName} → {preview.Replace('\n', ' ')}");

            // Returning a string (or a structured object) is what the function invocation middleware
            // turns into a tool result message for the LLM.
            return resultText;
        }
        catch (Exception ex)
        {
            var err = $"MCP call to {_toolName} on {_serverDisplay} failed: {ex.Message}";
            ToolExecutionTrace.Record($"   ❌ {err}");
            return err; // surface the error to the model so it can decide what to do
        }
    }
}