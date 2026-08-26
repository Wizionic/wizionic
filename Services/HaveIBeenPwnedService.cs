using System.Security.Cryptography;
using System.Text;

namespace App.Services;

/// <summary>
/// k-anonymity Pwned Passwords check (SHA-1 prefix only). A network failure does not
/// block setting a password — availability over a hard fail-closed.
/// </summary>
public sealed class HaveIBeenPwnedService
{
    private readonly IHttpClientFactory _http;

    public HaveIBeenPwnedService(IHttpClientFactory http) => _http = http;

    public async Task<bool> IsPwnedAsync(string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password))
            return false;

        string sha1;
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(password));
        sha1 = Convert.ToHexString(bytes);

        var prefix = sha1[..5];
        var suffix = sha1[5..];

        try
        {
            var client = _http.CreateClient("pwned");
            using var req = new HttpRequestMessage(HttpMethod.Get, "range/" + prefix);
            req.Headers.TryAddWithoutValidation("Add-Padding", "true");
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var reader = new StringReader(body);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var tab = line.IndexOf(':');
                if (tab <= 0)
                    continue;
                var found = line[..tab].Trim();
                if (found.Equals(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
