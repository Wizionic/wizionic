namespace App.Core.Setup;

/// <summary>
/// Controls visibility of the full-screen desktop setup wizard (Home Server + Lemonade).
/// </summary>
public interface ISetupWizardHost
{
    bool IsVisible { get; }

    /// <summary>True when first-run should open the wizard (not yet completed).</summary>
    bool ShouldAutoShow { get; }

    event Action? OnChanged;

    void Show();
    void Hide();

    /// <summary>Persist that the user finished (or skipped) the wizard.</summary>
    void MarkCompleted();
}
