using Microsoft.Extensions.AI;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Simple registry of AIFunction tools available to models.
/// These are app-level (not per-user-key) and enhance any chat that uses a tool-calling capable model.
/// This is the client-side copy for WASM (browser executes the tools directly).
/// </summary>
public interface IToolProvider
{
    IReadOnlyList<AITool> GetTools();
}
