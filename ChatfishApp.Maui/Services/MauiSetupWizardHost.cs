using ChatfishApp.Core.Homeserver;
using ChatfishApp.Core.Setup;

namespace ChatfishApp.Maui.Services;

public sealed class MauiSetupWizardHost : ISetupWizardHost
{
    private bool _visible;

    public MauiSetupWizardHost(bool autoShowOnFirstRun)
    {
        // Show until the user finishes or skips once. Re-run later from Settings.
        _ = autoShowOnFirstRun;
        ShouldAutoShow = !IsOnboardingCompleted();
        if (ShouldAutoShow)
            _visible = true;
    }

    public bool IsVisible => _visible;

    public bool ShouldAutoShow { get; private set; }

    public event Action? OnChanged;

    public void Show()
    {
        _visible = true;
        OnChanged?.Invoke();
    }

    public void Hide()
    {
        _visible = false;
        OnChanged?.Invoke();
    }

    public void MarkCompleted()
    {
        var state = HomeserverState.Load();
        state.OnboardingCompletedAt = DateTimeOffset.UtcNow;
        state.Save();
        ShouldAutoShow = false;
        Hide();
    }

    private static bool IsOnboardingCompleted()
    {
        try
        {
            return HomeserverState.Load().OnboardingCompletedAt.HasValue;
        }
        catch
        {
            return false;
        }
    }
}
