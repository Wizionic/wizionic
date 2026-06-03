// Deprecated - models moved to ProviderCatalog.cs (supports multi-provider + Gemini etc.)
// This file kept only to avoid breaking any external references during transition.
[Obsolete("Use ProviderCatalog instead")]
public static class GroqModels
{
    public static readonly string[] FreeModels =
    {
        "llama-3.1-8b-instant",
        "llama-3.3-70b-versatile",
        "qwen/qwen3-32b",
        "openai/gpt-oss-20b",
        "openai/gpt-oss-120b",
        "meta-llama/llama-4-scout-17b-16e-instruct",
        "allam-2-7b"
    };
}
