using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;

namespace RentNearBy.Infrastructure.Services;

// AES-256-GCM, stored as "v1.<base64 nonce>.<base64 ciphertext+tag>". The key (base64, 32 bytes) comes from
// IDENTITY_ENCRYPTION_KEY or Identity:EncryptionKey and is never generated here: a lost key means every
// stored value is unreadable. No rotation; "v1." is reserved so a re-encrypt job can mark "v2.".
public sealed class AesGcmIdentityCipher : IIdentityCipher
{
    public const string EnvKey = "IDENTITY_ENCRYPTION_KEY";
    public const string ConfigKey = "Identity:EncryptionKey";
    private const string Version = "v1";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("taxi-dl");

    private readonly byte[]? _key;

    public AesGcmIdentityCipher(IConfiguration configuration, ILogger<AesGcmIdentityCipher> logger)
    {
        _key = ParseKey(configuration[EnvKey]) ?? ParseKey(configuration[ConfigKey]);
        if (_key == null)
            logger.LogError("IDENTITY_ENCRYPTION_KEY not configured; taxi licence capture is disabled");
    }

    public bool IsConfigured => _key != null;

    public string Protect(string plain)
    {
        var key = _key ?? throw new InvalidOperationException("Identity cipher is not configured");
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var output = new byte[plainBytes.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, output.AsSpan(0, plainBytes.Length), output.AsSpan(plainBytes.Length), AssociatedData);
        return $"{Version}.{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(output)}";
    }

    public string Unprotect(string stored)
    {
        var key = _key ?? throw new InvalidOperationException("Identity cipher is not configured");
        var parts = (stored ?? string.Empty).Split('.');
        if (parts.Length != 3 || parts[0] != Version) throw new CryptographicException("Unsupported identity value");

        byte[] nonce, data;
        try
        {
            nonce = Convert.FromBase64String(parts[1]);
            data = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            throw new CryptographicException("Malformed identity value");
        }
        if (nonce.Length != NonceSize || data.Length < TagSize) throw new CryptographicException("Malformed identity value");

        var cipherLength = data.Length - TagSize;
        var plain = new byte[cipherLength];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, data.AsSpan(0, cipherLength), data.AsSpan(cipherLength), plain, AssociatedData);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[]? ParseKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var bytes = Convert.FromBase64String(value.Trim());
            return bytes.Length == KeySize ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
