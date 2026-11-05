namespace RentNearBy.Tests.Listings;

public class CacheStampTests
{
    [Fact]
    public void Wrap_ThenUnwrap_SameGeneration_ReturnsPayload()
        => Assert.Equal("{\"items\":[]}", CacheStamp.Unwrap(CacheStamp.Wrap(7, "{\"items\":[]}"), 7));

    [Fact]
    public void BumpAfterWrite_IsAMiss()
    {
        var stored = CacheStamp.Wrap(7, "[1,2]");
        Assert.Null(CacheStamp.Unwrap(stored, 8));
    }

    [Fact]
    public void ValueBuiltDuringABump_KeepsOldStamp_NeverServed()
    {
        var readGeneration = CacheStamp.Generation("4");
        var writtenLate = CacheStamp.Wrap(readGeneration, "[stale]");
        var afterBump = CacheStamp.Generation("5");

        Assert.Null(CacheStamp.Unwrap(writtenLate, afterBump));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"items\":[\"a|b\"]}")]
    [InlineData("[{\"name\":\"x|y\"}]")]
    [InlineData("|payload")]
    [InlineData("-1|payload")]
    [InlineData("abc|payload")]
    public void OldOrBrokenValues_AreMisses(string? raw)
        => Assert.Null(CacheStamp.Unwrap(raw, 0));

    [Fact]
    public void PayloadWithBars_IsKeptWhole()
        => Assert.Equal("a|b|c", CacheStamp.Unwrap("0|a|b|c", 0));

    [Theory]
    [InlineData(null, 0L)]
    [InlineData("", 0L)]
    [InlineData("12", 12L)]
    [InlineData("x", 0L)]
    public void Generation_MissingKeyIsZero(string? raw, long expected)
        => Assert.Equal(expected, CacheStamp.Generation(raw));

    [Fact]
    public void FirstWrite_BeforeAnyBump_UsesGenerationZero()
        => Assert.Equal("[]", CacheStamp.Unwrap(CacheStamp.Wrap(CacheStamp.Generation(null), "[]"), 0));
}
