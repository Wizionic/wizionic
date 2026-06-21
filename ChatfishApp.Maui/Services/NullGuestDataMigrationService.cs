using ChatfishApp.Core.Auth;

namespace ChatfishApp.Maui.Services;

public sealed class NullGuestDataMigrationService : IGuestDataMigrationService
{
    public Task MigrateIfNeededAsync() => Task.CompletedTask;
}