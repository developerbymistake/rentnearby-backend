using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// O2: request seats 4, 6, 7, 12; one request takes at most 12 travellers.
public class TaxiSeatsTests
{
    [Theory]
    [InlineData(4, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(12, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(8, false)]
    [InlineData(13, false)]
    [InlineData(-4, false)]
    public void Only_4_6_7_and_12_are_valid_seats(int seats, bool expected)
        => Assert.Equal(expected, TaxiRequestSeats.IsValid(seats));

    [Fact]
    public void Allowed_seats_match_the_driver_vehicle_options()
    {
        Assert.Equal([4, 6, 7, 12], TaxiRequestSeats.Allowed);
        Assert.Equal(12, TaxiRequestSeats.MaxTravellers);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 6)]
    [InlineData(6, 6)]
    [InlineData(7, 7)]
    [InlineData(8, 12)]
    [InlineData(12, 12)]
    [InlineData(15, 12)]
    public void Suggested_seats_are_the_smallest_that_hold_the_travellers(int travellers, int expected)
        => Assert.Equal(expected, TaxiRequestSeats.SuggestedFor(travellers));

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(4, 5, false)]
    [InlineData(6, 3, true)]
    [InlineData(7, 8, false)]
    [InlineData(12, 12, true)]
    public void Seats_hold_the_travellers_only_when_not_fewer(int seats, int travellers, bool expected)
        => Assert.Equal(expected, TaxiRequestSeats.Holds(seats, travellers));

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(6, 4, true)]
    [InlineData(4, 6, false)]
    [InlineData(7, 7, true)]
    [InlineData(6, 7, false)]
    [InlineData(12, 12, true)]
    [InlineData(12, 7, true)]
    [InlineData(7, 12, false)]
    public void A_listing_fits_when_its_seats_reach_the_request(int listing, int requested, bool expected)
        => Assert.Equal(expected, TaxiRequestSeats.ListingFits(listing, requested));

    [Fact]
    public void Suggested_seats_are_always_valid()
        => Assert.All(Enumerable.Range(1, 30), t => Assert.True(TaxiRequestSeats.IsValid(TaxiRequestSeats.SuggestedFor(t))));
}
