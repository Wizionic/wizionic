namespace App.Core.Auth;

public interface IGuestDataMigrationService
{
    Task MigrateIfNeededAsync();
}