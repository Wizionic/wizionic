namespace App.Core.Auth;

/// <summary>
/// Optional platform hook to persist or clear auth cookies (MAUI SQLite; browser uses native cookie jar).
/// </summary>
public interface IAuthSessionPersistence
{
    Task PersistCookiesAsync();
    Task ClearCookiesAsync();
}