namespace App.Core.Auth;

public sealed class AuthSetupStatus
{
    public bool FirstAdminRequired { get; init; }
    public bool CanBootstrap { get; init; }
}
