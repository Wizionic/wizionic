using System.Net;
using System.Text.Json;
using App.Core.Auth;
using App.Core.Configuration;

namespace App.Maui.Services;

/// <summary>
/// Persists the server auth cookie jar to SQLite so MAUI stays signed in across restarts.
/// </summary>
public sealed class MauiAuthCookieStore : IAuthSessionPersistence
{
    private const string StorageKey = "app-auth-cookies";

    private readonly SqliteSettingsDatabase _db;
    private readonly CookieContainer _container = new();
    private Uri _serverUri = new("http://localhost/");
    private bool _loaded;

    public CookieContainer Container => _container;

    public MauiAuthCookieStore(SqliteSettingsDatabase db) => _db = db;

    public void Configure(AppServerOptions options) =>
        _serverUri = options.BaseUri;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        await LoadAsync();
        _loaded = true;
    }

    public async Task PersistCookiesAsync() => await SaveAsync();

    public async Task ClearCookiesAsync()
    {
        try
        {
            foreach (Cookie cookie in _container.GetAllCookies())
                cookie.Expired = true;
        }
        catch
        {
            foreach (Cookie cookie in _container.GetCookies(_serverUri))
                cookie.Expired = true;
        }

        await _db.RemoveAsync(StorageKey);
    }

    private async Task LoadAsync()
    {
        try
        {
            var json = await _db.GetStringAsync(StorageKey);
            if (string.IsNullOrWhiteSpace(json)) return;

            var stored = JsonSerializer.Deserialize<List<StoredCookie>>(json);
            if (stored is null) return;

            foreach (var item in stored)
            {
                if (string.IsNullOrWhiteSpace(item.Name)) continue;

                var cookie = new Cookie(item.Name, item.Value, item.Path ?? "/", item.Domain ?? _serverUri.Host)
                {
                    Secure = item.Secure,
                    HttpOnly = item.HttpOnly
                };

                if (item.ExpiresUtc is not null)
                    cookie.Expires = item.ExpiresUtc.Value;

                try
                {
                    _container.Add(_serverUri, cookie);
                }
                catch
                {
                    // Skip malformed/expired entries.
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiAuthCookieStore] Load failed: {ex.Message}");
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var cookies = _container.GetCookies(_serverUri);
            if (cookies.Count == 0)
            {
                await _db.RemoveAsync(StorageKey);
                return;
            }

            var list = new List<StoredCookie>(cookies.Count);
            foreach (Cookie cookie in cookies)
            {
                if (cookie.Expired) continue;

                list.Add(new StoredCookie(
                    cookie.Name,
                    cookie.Value,
                    cookie.Domain,
                    cookie.Path,
                    cookie.Expires == DateTime.MinValue ? null : cookie.Expires.ToUniversalTime(),
                    cookie.Secure,
                    cookie.HttpOnly));
            }

            await _db.SetStringAsync(StorageKey, JsonSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiAuthCookieStore] Save failed: {ex.Message}");
        }
    }

    private sealed record StoredCookie(
        string Name,
        string Value,
        string? Domain,
        string? Path,
        DateTime? ExpiresUtc,
        bool Secure,
        bool HttpOnly);
}