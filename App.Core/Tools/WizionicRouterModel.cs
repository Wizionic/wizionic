namespace App.Core.Tools;

public static class WizionicRouterModel
{
    public const string LemonadePullId = "wizionic/Wizionic3.5-0.8B-router:Q5_K_M";
    public const string OllamaPullId = "hf.co/wizionic/Wizionic3.5-0.8B-router:Q5_K_M";
    public const string DisplayName = "Wizionic3.5 0.8B Router";
    public const string Description = "Wizionic fine-tuned Qwen3.5 0.8B for tool routing.";

    public static bool Matches(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Contains("Wizionic3.5", StringComparison.OrdinalIgnoreCase)
        && name.Contains("0.8B", StringComparison.OrdinalIgnoreCase);
}