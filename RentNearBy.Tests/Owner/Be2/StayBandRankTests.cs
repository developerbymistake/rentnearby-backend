using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Tests.Owner.Be2;

// T7: 5 km distance band, then paid rank, then distance; a price or a block never moves a stay.
public class StayBandRankTests
{
    private static StayNearbyDto S(string name, double km, short rank = 0) => new() { Id = Guid.NewGuid(), Name = name, DistanceKm = km, PriorityRank = rank };

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(4.99, 0)]
    [InlineData(5.0, 1)]
    [InlineData(12.4, 2)]
    public void Band_is_floor_of_km_over_five(double km, int band) => Assert.Equal(band, StayBandRank.Band(km));

    [Fact]
    public void Paid_rank_wins_only_inside_a_band()
    {
        var nearFree = S("near free", 1.0);
        var nearPaid = S("near paid", 4.0, 1);
        var farPaid = S("far paid", 6.0, 1);
        var midFree = S("mid free", 2.0);
        var ordered = StayBandRank.Order([farPaid, midFree, nearFree, nearPaid]).Select(s => s.Name).ToList();
        Assert.Equal(["near paid", "near free", "mid free", "far paid"], ordered);
    }

    [Fact]
    public void Ties_keep_the_input_order()
    {
        var a = S("a", 2.0);
        var b = S("b", 2.0);
        Assert.Equal(["a", "b"], StayBandRank.Order([a, b]).Select(s => s.Name));
    }

    [Fact]
    public void A_rule_or_a_block_never_changes_the_order()
    {
        var a = S("a", 1.0);
        var b = S("b", 2.0);
        b.FullOnDates = true;
        b.PriceChip = Core.DTOs.Owner.PriceChipDto.Of(40);
        b.DatesPerNight = 100;
        Assert.Equal(["a", "b"], StayBandRank.Order([b, a]).Select(s => s.Name));
    }

    [Fact]
    public void Radius_reads_use_the_band_order()
    {
        var centre = (Lat: 29.3805, Lng: 79.4605);
        var near = new StayNearbyDto { Id = Guid.NewGuid(), Name = "near", Latitude = 29.3815m, Longitude = 79.4605m };
        var farPaid = new StayNearbyDto { Id = Guid.NewGuid(), Name = "far paid", Latitude = 29.4400m, Longitude = 79.4605m, PriorityRank = 1 };
        var list = StayGeoRefine.WithinRadius([farPaid, near], centre.Lat, centre.Lng, 20);
        Assert.Equal(["near", "far paid"], list.Select(s => s.Name));
        var nearest = StayGeoRefine.Nearest([farPaid, near], centre.Lat, centre.Lng, 2);
        Assert.Equal(["near", "far paid"], nearest.Select(s => s.Name));
    }
}
