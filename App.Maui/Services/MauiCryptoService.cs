using System.Security.Cryptography;
using System.Text;
using App.Core.Storage;

namespace App.Maui.Services;

/// <summary>
/// AES-GCM compatible with WASM Web Crypto format: base64(IV || ciphertext || tag).
/// </summary>
public class MauiCryptoService : ICryptoService
{
    private const int IvSize = 12;
    private const int TagSize = 16;

    public Task<string> EncryptAsync(string keyBase64, string plaintext, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(keyBase64) || string.IsNullOrEmpty(plaintext))
            return Task.FromResult(plaintext ?? string.Empty);

        try
        {
            var key = Convert.FromBase64String(keyBase64);
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var iv = RandomNumberGenerator.GetBytes(IvSize);
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(iv, plainBytes, cipher, tag);

            var combined = new byte[IvSize + cipher.Length + TagSize];
            Buffer.BlockCopy(iv, 0, combined, 0, IvSize);
            Buffer.BlockCopy(cipher, 0, combined, IvSize, cipher.Length);
            Buffer.BlockCopy(tag, 0, combined, IvSize + cipher.Length, TagSize);

            return Task.FromResult(Convert.ToBase64String(combined));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiCrypto] Encrypt failed, falling back to plaintext: {ex.Message}");
            return Task.FromResult(plaintext);
        }
    }

    public Task<string> DecryptAsync(string keyBase64, string combinedBase64, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(keyBase64) || string.IsNullOrEmpty(combinedBase64))
            return Task.FromResult(combinedBase64 ?? string.Empty);

        try
        {
            var key = Convert.FromBase64String(keyBase64);
            var combined = Convert.FromBase64String(combinedBase64);
            if (combined.Length <= IvSize + TagSize)
                return Task.FromResult(combinedBase64);

            var iv = combined.AsSpan(0, IvSize);
            var cipherAndTag = combined.AsSpan(IvSize);
            var cipher = cipherAndTag[..^TagSize];
            var tag = cipherAndTag[^TagSize..];
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(iv, cipher, tag, plain);

            return Task.FromResult(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiCrypto] Decrypt failed (legacy plaintext or bad key?), returning as-is: {ex.Message}");
            return Task.FromResult(combinedBase64);
        }
    }
}