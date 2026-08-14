using App.Core.UI;

namespace App.Shared.Services;

public sealed class NullAppRestartService : IAppRestartService
{
    public static readonly NullAppRestartService Instance = new();

    private NullAppRestartService() { }

    public bool CanRestart => false;

    public void Restart() { }
}
