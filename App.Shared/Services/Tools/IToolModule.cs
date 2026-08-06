using Microsoft.Extensions.AI;

namespace App.Shared.Services.Tools;

/// <summary>
/// Injectable capability bundle that exposes AITools when configured and available.
/// </summary>
public interface IToolModule
{
    string ModuleName { get; }
    bool IsAvailable { get; }
    IReadOnlyList<AITool> GetTools();
}