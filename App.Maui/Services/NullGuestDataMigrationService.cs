using App.Core.Auth;

namespace App.Maui.Services;

public sealed class NullGuestDataMigrationService : IGuestDataMigrationService
{
    public Task MigrateIfNeededAsync() => Task.CompletedTask;
}