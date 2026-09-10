using App.Core.Homeserver;
using App.Core.Setup;

namespace App.Maui.Services;

public sealed class MauiSetupWizardHost : ISetupWizardHost
{
    private bool _visible;

    public MauiSetupWizardHost(bool autoShowOnFirstRun)
    {
        // A new Velopack install always shows the wizard, even if leftover ProgramData
        // still has onboardingCompletedAt from a previous Windows user or incomplete uninstall.
        if (autoShowOnFirstRun)
            ClearOnboardingFlags();

        ShouldAutoShow = autoShowOnFirstRun || !IsOnboardingCompleted();
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
        try
        {
            Directory.CreateDirectory(MauiAppData.Directory);
            File.WriteAllText(MauiAppData.OnboardingCompletedPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // still try machine-wide state below
        }

        try
        {
            var state = HomeserverState.Load();
            state.OnboardingCompletedAt = DateTimeOffset.UtcNow;
            state.Save();
        }
        catch
        {
            // per-user file is enough to skip on this Windows user
        }

        ShouldAutoShow = false;
        Hide();
    }

    private static bool IsOnboardingCompleted()
    {
        try
        {
            if (File.Exists(MauiAppData.OnboardingCompletedPath))
                return true;

            // Older builds only wrote ProgramData. Honor that on an upgrade that still
            // has this user's library; ignore it for a blank Windows profile.
            if (!HomeserverState.Load().OnboardingCompletedAt.HasValue)
                return false;

            return MauiAppData.HasLocalDatabase();
        }
        catch
        {
            return false;
        }
    }

    private static void ClearOnboardingFlags()
    {
        try
        {
            if (File.Exists(MauiAppData.OnboardingCompletedPath))
                File.Delete(MauiAppData.OnboardingCompletedPath);
        }
        catch
        {
            // ignore
        }

        try
        {
            var state = HomeserverState.Load();
            if (!state.OnboardingCompletedAt.HasValue)
                return;
            state.OnboardingCompletedAt = null;
            state.Save();
        }
        catch
        {
            // ProgramData may be admin-owned after an incomplete uninstall
        }
    }
}
