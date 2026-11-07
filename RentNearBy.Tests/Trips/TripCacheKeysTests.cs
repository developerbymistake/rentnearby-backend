namespace RentNearBy.Tests.Trips;

public class TripCacheKeysTests
{
    [Fact]
    public void Spots_Key()
        => Assert.Equal("trip:spots2:110000001", TripCacheKeys.Spots(110000001L));

    [Fact]
    public void Stale_Key()
        => Assert.Equal("trip:spots2:110000001:stale", TripCacheKeys.Stale(110000001L));

    [Fact]
    public void Lock_Key()
        => Assert.Equal("trip:spots2:110000001:lock", TripCacheKeys.Lock(110000001L));

    [Fact]
    public void Matrix_Keys()
    {
        Assert.Equal("trip:matrix:110000001", TripCacheKeys.Matrix(110000001L));
        Assert.Equal("trip:matrix:110000001:lock", TripCacheKeys.MatrixLock(110000001L));
    }

    [Fact]
    public void Keys_AreDistinct()
    {
        const long key = 110000003L;
        var keys = new[] { TripCacheKeys.Spots(key), TripCacheKeys.Stale(key), TripCacheKeys.Lock(key), TripCacheKeys.Matrix(key), TripCacheKeys.MatrixLock(key) };
        Assert.Equal(5, keys.Distinct().Count());
    }

    [Fact]
    public void Route_Key_IsSha256OfLonLatAtFiveDecimals()
        => Assert.Equal("trip:route:adb0cb47c12cfd1db4990f1b5237b5a48e7f4d218dfeac25801c0d8829ba5c65",
            TripCacheKeys.Route(new[] { (29.3919, 79.4542), (29.4, 79.47) }));

    [Fact]
    public void Route_Key_IgnoresNoiseBeyondFiveDecimals()
        => Assert.Equal(TripCacheKeys.Route(new[] { (29.3919, 79.4542), (29.4, 79.47) }),
            TripCacheKeys.Route(new[] { (29.391900004, 79.454199996), (29.4000001, 79.47) }));

    [Fact]
    public void Route_Key_DependsOnOrder()
        => Assert.NotEqual(TripCacheKeys.Route(new[] { (29.3919, 79.4542), (29.4, 79.47) }),
            TripCacheKeys.Route(new[] { (29.4, 79.47), (29.3919, 79.4542) }));

    [Fact]
    public void Route_Key_IsCultureInvariant()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("trip:route:adb0cb47c12cfd1db4990f1b5237b5a48e7f4d218dfeac25801c0d8829ba5c65",
                TripCacheKeys.Route(new[] { (29.3919, 79.4542), (29.4, 79.47) }));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
