using System.Security.Cryptography;
using System.Text;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.PlayBilling;

public class PlayAccountKeysTests
{
    private static readonly string Salt = new('k', 32);

    [Fact]
    public void Compute_IsSixtyFourLowerHexChars()
    {
        var key = PlayAccountKeys.Compute(Salt, TestIds.User1);

        Assert.Equal(PlayRules.ObfuscatedIdLength, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void Compute_SameInput_SameKey()
        => Assert.Equal(PlayAccountKeys.Compute(Salt, TestIds.User1), PlayAccountKeys.Compute(Salt, TestIds.User1));

    [Fact]
    public void Compute_DifferentSalt_DifferentKey()
        => Assert.NotEqual(PlayAccountKeys.Compute(Salt, TestIds.User1), PlayAccountKeys.Compute(new string('j', 32), TestIds.User1));

    [Fact]
    public void Compute_DifferentUser_DifferentKey()
        => Assert.NotEqual(PlayAccountKeys.Compute(Salt, TestIds.User1), PlayAccountKeys.Compute(Salt, TestIds.User2));

    [Fact]
    public void Compute_IsHmacSha256OfDashedGuid()
    {
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Salt),
            Encoding.UTF8.GetBytes("c0000000-0000-0000-0000-000000000001"))).ToLowerInvariant();

        Assert.Equal(expected, PlayAccountKeys.Compute(Salt, TestIds.User1));
    }
}
