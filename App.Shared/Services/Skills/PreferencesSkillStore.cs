using App.Core.Skills;
using App.Core.Sync;

namespace App.Shared.Services.Skills;

/// <summary>
/// Skill library backed by <see cref="ISyncPreferencesStore"/> (works on WASM and MAUI).
/// </summary>
public sealed class PreferencesSkillStore : SkillStoreBase
{
    public const string StorageKey = "app-skills-library-v1";

    private readonly ISyncPreferencesStore _prefs;

    public PreferencesSkillStore(ISyncPreferencesStore prefs)
    {
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
    }

    protected override Task<string?> ReadJsonAsync(CancellationToken ct) =>
        _prefs.GetStringAsync(StorageKey, ct);

    protected override Task WriteJsonAsync(string json, CancellationToken ct) =>
        _prefs.SetStringAsync(StorageKey, json, ct);
}
