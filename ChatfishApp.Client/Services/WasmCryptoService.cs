using Microsoft.JSInterop;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Thin wrapper around browser Web Crypto (AES-GCM) exposed via IJSRuntime.
/// Used by WasmConversationStore / WasmKeyStore to encrypt the JSON blobs stored
/// in localStorage (or IndexedDB) and by the live sync client to encrypt payloads
/// before they cross the relay.
///
/// The key bytes come from the server (User.LocalEncryptionKey, unprotected for
/// the authenticated client only). The JS side does the actual subtle crypto work
/// (see the helpers added to the host App.razor script).
/// </summary>
public class WasmCryptoService
{
    private readonly IJSRuntime _js;

    public WasmCryptoService(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>
    /// Encrypts a plaintext string (usually a JSON blob) and returns a single
    /// base64 string containing IV || ciphertext (so it is easy to store in one
    /// localStorage / IndexedDB entry).
    /// </summary>
    public async Task<string> EncryptAsync(string keyBase64, string plaintext)
    {
        if (string.IsNullOrEmpty(keyBase64) || string.IsNullOrEmpty(plaintext))
            return plaintext ?? string.Empty;

        try
        {
            return await _js.InvokeAsync<string>("encryptLocalData", keyBase64, plaintext);
        }
        catch (Exception ex)
        {
            // In a real app you might want to surface this differently.
            // For now fall back to plaintext so the app doesn't completely break
            // if the crypto helpers are missing during early development.
            Console.WriteLine($"[WasmCrypto] Encrypt failed, falling back to plaintext: {ex.Message}");
            return plaintext;
        }
    }

    /// <summary>
    /// Reverses EncryptAsync. If the value is not a valid combined blob (or the
    /// key is wrong), the JS side will throw and we fall back to returning the
    /// original value (treating it as legacy plaintext).
    /// </summary>
    public async Task<string> DecryptAsync(string keyBase64, string combinedBase64)
    {
        if (string.IsNullOrEmpty(keyBase64) || string.IsNullOrEmpty(combinedBase64))
            return combinedBase64 ?? string.Empty;

        try
        {
            return await _js.InvokeAsync<string>("decryptLocalData", keyBase64, combinedBase64);
        }
        catch (Exception ex)
        {
            // Likely a legacy plaintext value or a tampered blob.
            Console.WriteLine($"[WasmCrypto] Decrypt failed (legacy plaintext or bad key?), returning as-is: {ex.Message}");
            return combinedBase64;
        }
    }
}