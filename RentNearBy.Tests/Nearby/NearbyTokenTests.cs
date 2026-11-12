namespace RentNearBy.Tests.Nearby;

public class NearbyTokenTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(1234.5678901234567)]
    [InlineData(420000.00000000006)]
    [InlineData(1e-9)]
    public void Round_trip_keeps_the_exact_double_and_id(double d)
    {
        var id = Guid.NewGuid();
        var s = new NearbyToken(d, id).Encode();
        Assert.True(NearbyToken.TryDecode(s, out var t));
        Assert.Equal(d, t!.DistanceMeters);
        Assert.Equal(id, t.Id);
    }

    [Fact]
    public void Encoded_token_is_url_safe()
    {
        var s = new NearbyToken(98765.4321, Guid.NewGuid()).Encode();
        Assert.DoesNotContain('+', s);
        Assert.DoesNotContain('/', s);
        Assert.DoesNotContain('=', s);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a token")]
    [InlineData("!!!!")]
    public void Garbage_is_rejected(string? s)
    {
        Assert.False(NearbyToken.TryDecode(s, out var t));
        Assert.Null(t);
    }

    [Fact]
    public void Wrong_shapes_are_rejected()
    {
        static string B(string raw) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var g = Guid.NewGuid().ToString("N");
        Assert.False(NearbyToken.TryDecode(B("12.5"), out _));
        Assert.False(NearbyToken.TryDecode(B("abc|" + g), out _));
        Assert.False(NearbyToken.TryDecode(B("NaN|" + g), out _));
        Assert.False(NearbyToken.TryDecode(B("Infinity|" + g), out _));
        Assert.False(NearbyToken.TryDecode(B("-5|" + g), out _));
        Assert.False(NearbyToken.TryDecode(B("5|not-a-guid"), out _));
        Assert.False(NearbyToken.TryDecode(B("5|" + g + "|x"), out _));
        Assert.False(NearbyToken.TryDecode(new string('A', 500), out _));
    }
}
