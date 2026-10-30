using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Infrastructure;

public class AesGcmIdentityCipherTests
{
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());
    private const string Licence = "UK0720150012345";

    private static AesGcmIdentityCipher Cipher(string? envKey, string? configKey = null)
    {
        var values = new List<KeyValuePair<string, string?>>();
        if (envKey != null) values.Add(new(AesGcmIdentityCipher.EnvKey, envKey));
        if (configKey != null) values.Add(new(AesGcmIdentityCipher.ConfigKey, configKey));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new AesGcmIdentityCipher(configuration, NullLogger<AesGcmIdentityCipher>.Instance);
    }

    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        var cipher = Cipher(KeyA);
        var stored = cipher.Protect(Licence);

        Assert.True(cipher.IsConfigured);
        Assert.Equal(Licence, cipher.Unprotect(stored));
    }

    [Fact]
    public void Protect_Format_V1NonceCiphertext()
    {
        var stored = Cipher(KeyA).Protect(Licence);
        var parts = stored.Split('.');

        Assert.Equal(3, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.Equal(12, Convert.FromBase64String(parts[1]).Length);
        Assert.Equal(Licence.Length + 16, Convert.FromBase64String(parts[2]).Length);
        Assert.DoesNotContain(Licence, stored);
        Assert.True(stored.Length <= TaxiIdentityRules.DlEncMaxLength);
    }

    [Fact]
    public void Protect_SamePlain_DistinctNoncePerCall()
    {
        var cipher = Cipher(KeyA);

        Assert.NotEqual(cipher.Protect(Licence), cipher.Protect(Licence));
    }

    [Fact]
    public void Unprotect_WrongKey_Throws()
    {
        var stored = Cipher(KeyA).Protect(Licence);

        Assert.ThrowsAny<CryptographicException>(() => Cipher(KeyB).Unprotect(stored));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var cipher = Cipher(KeyA);
        var parts = cipher.Protect(Licence).Split('.');
        var data = Convert.FromBase64String(parts[2]);
        data[0] ^= 0x01;
        var tampered = $"{parts[0]}.{parts[1]}.{Convert.ToBase64String(data)}";

        Assert.ThrowsAny<CryptographicException>(() => cipher.Unprotect(tampered));
    }

    [Theory]
    [InlineData("v2.AAAA.AAAA")]
    [InlineData("not-a-blob")]
    [InlineData("v1.%%%.%%%")]
    [InlineData("v1.AAAA.AAAA")]
    public void Unprotect_Malformed_Throws(string stored)
        => Assert.ThrowsAny<CryptographicException>(() => Cipher(KeyA).Unprotect(stored));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("c2hvcnQ=")]          // 5 bytes
    [InlineData("not base64 at all")]
    public void IsConfigured_FalseOnMissingOrInvalidKey(string? key)
        => Assert.False(Cipher(key).IsConfigured);

    [Fact]
    public void ConfigSection_UsedWhenEnvMissing()
        => Assert.True(Cipher(null, KeyA).IsConfigured);

    [Fact]
    public void Protect_NotConfigured_Throws()
        => Assert.Throws<InvalidOperationException>(() => Cipher(null).Protect(Licence));
}
