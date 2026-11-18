using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

public class TaxiSeatsTests
{
    [Theory]
    [InlineData(4, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(8, false)]
    [InlineData(-4, false)]
    public void Only_4_6_and_7_are_valid_seats(int seats, bool expected)
        => Assert.Equal(expected, TaxiRequestSeats.IsValid(seats));

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 4)]
    [InlineData(5, 6)]
    [InlineData(6, 6)]
    [InlineData(7, 7)]
    [InlineData(15, 7)]
    public void Suggested_seats_follow_the_travellers(int travellers, int expected)
        => Assert.Equal(expected, TaxiRequestSeats.SuggestedFor(travellers));

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(6, 4, true)]
    [InlineData(4, 6, false)]
    [InlineData(7, 7, true)]
    [InlineData(6, 7, false)]
    public void A_listing_fits_when_its_seats_reach_the_request(int listing, int requested, bool expected)
        => Assert.Equal(expected, TaxiRequestSeats.ListingFits(listing, requested));

    [Fact]
    public void Suggested_seats_are_always_valid()
        => Assert.All(Enumerable.Range(1, 30), t => Assert.True(TaxiRequestSeats.IsValid(TaxiRequestSeats.SuggestedFor(t))));
}
