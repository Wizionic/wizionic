namespace ChatfishApp.Core.Auth;

public interface IGuestDataMigrationService
{
    Task MigrateIfNeededAsync();
}