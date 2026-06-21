using Microsoft.Extensions.AI;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// Built-in app tools without MCP extensions (used on MAUI and as a fallback).
/// </summary>
public sealed class NativeToolProvider : IToolProvider
{
    private readonly IReadOnlyList<AITool> _tools =
    [
        AIFunctionFactory.Create(AppTools.SearchWeb),
        AIFunctionFactory.Create(AppTools.SummarizeUrl),
        AIFunctionFactory.Create(AppTools.GetCurrentTimeUtc),
        AIFunctionFactory.Create(AppTools.Calculate),
        AIFunctionFactory.Create(AppTools.GetCurrentWeather)
    ];

    public IReadOnlyList<AITool> GetTools() => _tools;
}