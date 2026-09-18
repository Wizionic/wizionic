using App.Core.Auth;
using App.Core.Lemonade;
using App.Core.Ollama;
using App.Core.Storage;

namespace App.Shared.Services;

/// <summary>
/// After sign-in on desktop, refresh Lemonade/Ollama catalogs into the user keystore
/// (setup wizard writes those under the guest prefix). Single-flight per user so
/// Chat and layout bootstrap share one request.
/// </summary>
public sealed class LocalAiCatalogWarmup
{
    private readonly IKeyStore _keyStore;
    private readonly IAuthService _auth;
    private readonly ILemonadeInstallService _lemonade;
    private readonly IOllamaInstallService _ollama;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private string? _doneForUser;
    private string? _inFlightUser;
    private Task? _inFlight;

    public LocalAiCatalogWarmup(
        IKeyStore keyStore,
        IAuthService auth,
        ILemonadeInstallService lemonade,
        IOllamaInstallService ollama,
        HttpClient http)
    {
        _keyStore = keyStore;
        _auth = auth;
        _lemonade = lemonade;
        _ollama = ollama;
        _http = http;
    }

    public Task EnsureForCurrentUserAsync()
    {
        if (!AppEnvironment.IsMaui || !_auth.IsAuthenticated)
            return Task.CompletedTask;

        var user = _auth.UserId ?? _auth.Email;
        if (string.IsNullOrEmpty(user))
            return Task.CompletedTask;

        lock (_gate)
        {
            if (string.Equals(_doneForUser, user, StringComparison.Ordinal))
                return Task.CompletedTask;
            if (_inFlight is not null && string.Equals(_inFlightUser, user, StringComparison.Ordinal))
                return _inFlight;

            var run = RunAsync(user);
            _inFlight = run;
            _inFlightUser = user;
            return run;
        }
    }

    private async Task RunAsync(string user)
    {
        try
        {
            await _keyStore.LoadAsync();

            var lemonadeNeeded = _lemonade.IsSupported && _lemonade.IsInstalled;
            var ollamaNeeded = _ollama.IsSupported && _ollama.IsInstalled;
            var lemonadeOk = !lemonadeNeeded;
            var ollamaOk = !ollamaNeeded;

            if (lemonadeNeeded)
            {
                if (string.IsNullOrWhiteSpace(_keyStore.LemonadeBaseUrl))
                    await _keyStore.SetLemonadeBaseUrlAsync(_lemonade.DefaultBaseUrl);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await _keyStore.RefreshLemonadeModelsFromServerAsync(
                        _http, _lemonade.DefaultBaseUrl, null, cts.Token);
                    lemonadeOk = true;
                }
                catch
                {
                    lemonadeOk = false;
                }
            }

            if (ollamaNeeded)
            {
                if (string.IsNullOrWhiteSpace(_keyStore.OllamaBaseUrl))
                    await _keyStore.SetOllamaBaseUrlAsync(_ollama.DefaultBaseUrl);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await _keyStore.RefreshOllamaModelsFromServerAsync(
                        _http, _ollama.DefaultBaseUrl, cts.Token);
                    ollamaOk = true;
                }
                catch
                {
                    ollamaOk = false;
                }
            }

            if (lemonadeOk && ollamaOk)
            {
                lock (_gate)
                    _doneForUser = user;
            }
        }
        catch
        {
            // Chat still loads; a later Ensure retries until success.
        }
        finally
        {
            lock (_gate)
            {
                if (string.Equals(_inFlightUser, user, StringComparison.Ordinal))
                {
                    _inFlight = null;
                    _inFlightUser = null;
                }
            }
        }
    }
}
