using App.Core.Skills;

namespace App.Shared.Services.Skills;

/// <summary>No-op skill runner for host SSR / environments without chat completion.</summary>
public sealed class NullSkillRunner : ISkillRunner
{
    public static readonly NullSkillRunner Instance = new();

    public Task<SkillRunResult> RunAsync(SkillRunRequest request, CancellationToken ct = default) =>
        Task.FromResult(new SkillRunResult
        {
            Success = false,
            Error = "Skills run in the app client (WASM or MAUI), not on the host shell."
        });
}
