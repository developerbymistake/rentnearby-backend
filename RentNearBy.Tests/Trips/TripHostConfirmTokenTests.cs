using System.Text.RegularExpressions;

namespace RentNearBy.Tests.Trips;

public class TripHostConfirmTokenTests
{
    [Fact]
    public void Generate_is_43_url_safe_characters()
    {
        for (var i = 0; i < 50; i++)
            Assert.Matches(new Regex("^[A-Za-z0-9_-]{43}$"), HostConfirmTokens.Generate());
    }

    [Fact]
    public void Generate_has_no_padding()
    {
        Assert.DoesNotContain("=", HostConfirmTokens.Generate());
    }

    [Fact]
    public void Two_tokens_differ()
    {
        var set = Enumerable.Range(0, 100).Select(_ => HostConfirmTokens.Generate()).ToHashSet();
        Assert.Equal(100, set.Count);
    }

    [Fact]
    public void Hash_is_64_lowercase_hex()
    {
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), HostConfirmTokens.Hash(HostConfirmTokens.Generate()));
    }

    [Fact]
    public void Hash_is_sha256_of_the_token()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", HostConfirmTokens.Hash("abc"));
    }

    [Fact]
    public void Hash_is_stable_and_differs_per_token()
    {
        var a = HostConfirmTokens.Generate();
        var b = HostConfirmTokens.Generate();
        Assert.Equal(HostConfirmTokens.Hash(a), HostConfirmTokens.Hash(a));
        Assert.NotEqual(HostConfirmTokens.Hash(a), HostConfirmTokens.Hash(b));
        Assert.NotEqual(a, HostConfirmTokens.Hash(a));
    }
}
