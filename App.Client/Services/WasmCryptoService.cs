using App.Core.Storage;
using Microsoft.JSInterop;

namespace App.Client.Services;

/// <summary>
/// Browser Web Crypto (AES-GCM) implementation of <see cref="ICryptoService"/>.
/// </summary>
public class WasmCryptoService : ICryptoService
{
    private readonly IJSRuntime _js;

    public WasmCryptoService(IJSRuntime js) => _js = js;

    public async Task<string> EncryptAsync(string keyBase64, string plaintext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(keyBase64) || string.IsNullOrEmpty(plaintext))
            return plaintext ?? string.Empty;

        try
        {
            return await _js.InvokeAsync<string>("encryptLocalData", ct, keyBase64, plaintext);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmCrypto] Encrypt failed, falling back to plaintext: {ex.Message}");
            return plaintext;
        }
    }

    public async Task<string> DecryptAsync(string keyBase64, string combinedBase64, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(keyBase64) || string.IsNullOrEmpty(combinedBase64))
            return combinedBase64 ?? string.Empty;

        try
        {
            return await _js.InvokeAsync<string>("decryptLocalData", ct, keyBase64, combinedBase64);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmCrypto] Decrypt failed (legacy plaintext or bad key?), returning as-is: {ex.Message}");
            return combinedBase64;
        }
    }
}