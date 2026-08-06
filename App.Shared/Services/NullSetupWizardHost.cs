using App.Core.Setup;

namespace App.Shared.Services;

public sealed class NullSetupWizardHost : ISetupWizardHost
{
    public static readonly NullSetupWizardHost Instance = new();

    private NullSetupWizardHost() { }

    public bool IsVisible => false;
    public bool ShouldAutoShow => false;

    public event Action? OnChanged
    {
        add { }
        remove { }
    }

    public void Show() { }
    public void Hide() { }
    public void MarkCompleted() { }
}
