namespace RentNearBy.Tests.Trips;

public class TaxiWaysKeysTests
{
    [Fact]
    public void Atlas_place_key_uses_the_city_key() =>
        Assert.Equal("a210000123", TaxiWaysKeys.PlaceKeyAtlas(210000123));

    [Fact]
    public void Plan_key_joins_places_and_round_trips()
    {
        var key = TaxiWaysKeys.PlanKey(new[] { "a1", "a2", "a3" });
        Assert.Equal("p1~a1~a2~a3", key);
        Assert.True(TaxiWaysKeys.IsValidKey(key));
    }

    [Fact]
    public void Leg_key_has_two_places()
    {
        var key = TaxiWaysKeys.LegKey("a1", "a2");
        Assert.Equal("l1~a1~a2", key);
        Assert.True(TaxiWaysKeys.IsValidKey(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("p1~a1 a2")]
    [InlineData("p1~a1/../x")]
    [InlineData("p1~a1;drop")]
    public void Invalid_keys_are_rejected(string key) => Assert.False(TaxiWaysKeys.IsValidKey(key));

    [Fact]
    public void Overlong_key_is_rejected() => Assert.False(TaxiWaysKeys.IsValidKey("p1~" + new string('a', 250)));

    [Fact]
    public void Cache_keys_carry_the_version()
    {
        Assert.Equal("taxi:ways:ver", TaxiWaysKeys.Ver);
        Assert.NotEqual(TaxiWaysKeys.Plan("1", "a1", "a2", 2), TaxiWaysKeys.Plan("2", "a1", "a2", 2));
        Assert.NotEqual(TaxiWaysKeys.Plan("1", "a1", "a2", 2), TaxiWaysKeys.Plan("1", "a1", "a2", 3));
    }
}
