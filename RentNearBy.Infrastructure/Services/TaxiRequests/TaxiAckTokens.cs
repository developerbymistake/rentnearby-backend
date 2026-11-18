using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// Ack token of a ring: HMAC-SHA256 over the offer id, key derived from the JWT signing key and the label "taxi-ack-v1".
// base64url of the first 16 bytes; the check is constant time.
public sealed class TaxiAckTokens
{
    public const string KeyLabel = "taxi-ack-v1";
    public const int TokenBytes = 16;

    private readonly byte[] _key;

    public TaxiAckTokens(string jwtSigningKey)
    {
        if (string.IsNullOrEmpty(jwtSigningKey)) throw new InvalidOperationException("JWT Key not configured");
        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwtSigningKey), Encoding.UTF8.GetBytes(KeyLabel));
    }

    public TaxiAckTokens(IConfiguration configuration)
        : this(configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured")) { }

    public string Create(Guid offerId) => Base64Url(Mac(offerId));

    public bool IsValid(Guid offerId, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != Base64UrlLength) return false;
        byte[] given;
        try { given = FromBase64Url(token); }
        catch (FormatException) { return false; }
        // The last character carries spare bits; only the canonical text of the bytes is accepted.
        return given.Length == TokenBytes && string.Equals(Base64Url(given), token, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(given, Mac(offerId));
    }

    private static int Base64UrlLength => (TokenBytes * 4 + 2) / 3;

    private byte[] Mac(Guid offerId)
        => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(offerId.ToString("D")))[..TokenBytes];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
        return Convert.FromBase64String(b);
    }
}
