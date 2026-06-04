using Microsoft.Extensions.AI;

namespace ChatfishApp.Services.Tools;

/// <summary>
/// Simple registry of AIFunction tools available to models (WASM/client-side version).
/// Tools run in the browser when using WASM chat.
/// </summary>
public class DefaultToolProvider : IToolProvider
{
    private readonly List<AITool> _tools;

    public DefaultToolProvider()
    {
        // Register the tools we want models to be able to call.
        // The descriptions + parameter metadata are what the model sees.
        _tools = new List<AITool>
        {
            AIFunctionFactory.Create(AppTools.SearchWeb),
            AIFunctionFactory.Create(AppTools.SummarizeUrl),
            AIFunctionFactory.Create(AppTools.GetCurrentTimeUtc),
            AIFunctionFactory.Create(AppTools.Calculate),
            AIFunctionFactory.Create(AppTools.GetCurrentWeather)
        };
    }

    public IReadOnlyList<AITool> GetTools() => _tools;
}
