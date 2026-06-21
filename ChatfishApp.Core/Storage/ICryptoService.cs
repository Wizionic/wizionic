namespace ChatfishApp.Core.Storage;

public interface ICryptoService
{
    Task<string> EncryptAsync(string keyBase64, string plaintext, CancellationToken ct = default);
    Task<string> DecryptAsync(string keyBase64, string combinedBase64, CancellationToken ct = default);
}