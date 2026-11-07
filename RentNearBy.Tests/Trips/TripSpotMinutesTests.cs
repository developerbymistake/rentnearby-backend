using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.Models;

namespace RentNearBy.Tests.Trips;

public class TripSpotMinutesTests
{
    private static CityRoadMatrix Matrix() => new()
    {
        Ids = ["a", "b", "c"],
        Sec = [0, 61, -1, 120, 0, 3600, -1, 59, 0],
        Metres = [0, 1000, -1, 1000, 0, 1000, -1, 1000, 0],
    };

    [Fact]
    public void Build_RoundsUpToWholeMinutes()
    {
        var m = TripSpotMinutes.Build(Matrix(), ["a", "b"]);
        Assert.Equal(2, m[0][1]);
        Assert.Equal(2, m[1][0]);
        Assert.Equal(0, m[0][0]);
    }

    [Fact]
    public void Build_NoRoute_IsNull()
    {
        var m = TripSpotMinutes.Build(Matrix(), ["a", "c"]);
        Assert.Null(m[0][1]);
        Assert.Null(m[1][0]);
    }

    [Fact]
    public void Build_SliceFollowsRequestOrder()
    {
        var m = TripSpotMinutes.Build(Matrix(), ["c", "b"]);
        Assert.Equal(1, m[0][1]);
        Assert.Equal(60, TripSpotMinutes.Build(Matrix(), ["b", "c"])[0][1]);
    }

    [Fact]
    public void Build_UnknownId_RowAndColumnNull()
    {
        var m = TripSpotMinutes.Build(Matrix(), ["a", "zzz"]);
        Assert.Null(m[0][1]);
        Assert.Null(m[1][0]);
        Assert.Null(m[1][1]);
        Assert.Equal(0, m[0][0]);
    }

    [Fact]
    public void Build_EmptyList_Empty() => Assert.Empty(TripSpotMinutes.Build(Matrix(), []));

    private static readonly SpotMinutesRequestValidator V = new();

    [Fact]
    public void Validator_AcceptsUpTo25()
        => Assert.True(V.Validate(new SpotMinutesRequest { SpotIds = Enumerable.Range(0, 25).Select(i => $"s{i}").ToList() }).IsValid);

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public void Validator_RejectsEmptyAndTooMany(int count)
        => Assert.False(V.Validate(new SpotMinutesRequest { SpotIds = Enumerable.Range(0, count).Select(i => $"s{i}").ToList() }).IsValid);

    [Fact]
    public void Validator_RejectsNullListBlankAndLongIds()
    {
        Assert.False(V.Validate(new SpotMinutesRequest()).IsValid);
        Assert.False(V.Validate(new SpotMinutesRequest { SpotIds = ["a", " "] }).IsValid);
        Assert.False(V.Validate(new SpotMinutesRequest { SpotIds = [new string('x', 101)] }).IsValid);
    }
}
