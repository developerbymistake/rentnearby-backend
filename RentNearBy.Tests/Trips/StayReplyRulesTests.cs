using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Trips;

public class StayReplyRulesTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(4, 4, false)]
    [InlineData(5, 4, true)]
    [InlineData(5, 3, false)]
    [InlineData(10, 7, true)]
    [InlineData(10, 6, false)]
    public void Fast_needs_history_and_seventy_percent(int received, int answered, bool fast)
        => Assert.Equal(fast, StayReplyRules.RepliesFast(received, answered));

    [Fact]
    public void Dto_flag_is_only_true_or_absent()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new StayNearbyDto());
        Assert.DoesNotContain("RepliesFast", json);
    }
}

public class TripSimilarStaysTests
{
    private static readonly Guid Homestay = Guid.NewGuid();
    private static readonly Guid Villa = Guid.NewGuid();

    private static StayNearbyDto Stay(int price, Guid? type = null, bool fast = false, short rank = 0, bool? fits = true, bool? full = null) => new()
    {
        Id = Guid.NewGuid(),
        StayTypeId = type ?? Homestay,
        MinPricePerNight = price,
        RepliesFast = fast ? true : null,
        PriorityRank = rank,
        Fits = fits,
        FullOnDates = full,
        Latitude = 29.38m,
        Longitude = 79.45m,
    };

    [Theory]
    [InlineData("stay:1", 1)]
    [InlineData("stay:12", 12)]
    [InlineData("taxi", null)]
    [InlineData("between:2", null)]
    [InlineData("stay:0", null)]
    public void Only_stay_steps_have_a_leg(string key, int? leg) => Assert.Equal(leg, TripSimilarStays.LegOfStep(key));

    [Theory]
    [InlineData(2800, 2000, true)]
    [InlineData(2800, 3640, true)]
    [InlineData(2800, 1950, false)]
    [InlineData(2800, 3700, false)]
    public void Price_within_thirty_percent(int reference, int price, bool close)
        => Assert.Equal(close, TripSimilarStays.PriceClose(price, reference));

    [Fact]
    public void Picks_same_type_close_price_fitting_free_stays_fast_hosts_first()
    {
        var slow = Stay(2800);
        var fast = Stay(3000, fast: true);
        var villa = Stay(2800, Villa);
        var dear = Stay(9000);
        var tooSmall = Stay(2800, fits: false);
        var full = Stay(2800, full: true);
        var asked = Stay(2800);
        var picked = TripSimilarStays.Pick([slow, fast, villa, dear, tooSmall, full, asked], Homestay, 2800, (29.38, 79.45),
            new HashSet<Guid> { asked.Id }, 3);
        Assert.Equal(new[] { fast.Id, slow.Id }, picked.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Takes_at_most_the_limit()
    {
        var all = Enumerable.Range(0, 6).Select(_ => Stay(2800)).ToList();
        Assert.Equal(2, TripSimilarStays.Pick(all, Homestay, 2800, null, new HashSet<Guid>(), 2).Count);
    }
}
