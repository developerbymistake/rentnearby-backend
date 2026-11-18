using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiAckTokensTests
{
    private static readonly Guid A = Guid.Parse("a1b2c3d4-0001-4a00-8000-000000000001");
    private static readonly Guid B = Guid.Parse("a1b2c3d4-0002-4a00-8000-000000000002");
    private static TaxiAckTokens Tokens(string key = "test-signing-key-test-signing-key-123") => new(key);

    [Fact]
    public void Ack_token_round_trips()
    {
        var tokens = Tokens();
        var token = tokens.Create(A);

        Assert.Equal(22, token.Length);
        Assert.DoesNotContain(token, c => c is '+' or '/' or '=');
        Assert.True(tokens.IsValid(A, token));
        Assert.Equal(token, Tokens().Create(A));
    }

    [Fact]
    public void Ack_token_differs_per_offer()
    {
        var tokens = Tokens();
        Assert.NotEqual(tokens.Create(A), tokens.Create(B));
        Assert.False(tokens.IsValid(B, tokens.Create(A)));
        Assert.NotEqual(tokens.Create(A), Tokens("another-signing-key-another-key-4567").Create(A));
    }

    [Fact]
    public void Tampered_token_fails()
    {
        var tokens = Tokens();
        var token = tokens.Create(A);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

        for (var i = 0; i < token.Length; i++)
            foreach (var c in alphabet)
            {
                if (c == token[i]) continue;
                var bad = token[..i] + c + token[(i + 1)..];
                Assert.False(tokens.IsValid(A, bad), $"position {i} char {c}");
            }

        Assert.False(tokens.IsValid(A, token[..^1]));
        Assert.False(tokens.IsValid(A, token + "A"));
        Assert.False(tokens.IsValid(A, ""));
        Assert.False(tokens.IsValid(A, null));
        Assert.False(tokens.IsValid(A, "!!!!!!!!!!!!!!!!!!!!!!"));
    }
}
