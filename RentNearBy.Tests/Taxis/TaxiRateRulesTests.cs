using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Taxis;

public class TaxiRateRulesTests
{
    private static TaxiListing Taxi(int? rate = 1800, int? km = 80, params int[] fares)
    {
        var t = new TaxiListing { DrivingLicenceNumberEnc = "x", DateOfBirth = new DateOnly(1985, 1, 1), LocalDayRate = rate, LocalDayKm = km };
        foreach (var (f, i) in (fares.Length == 0 ? [1500] : fares).Select((f, i) => (f, i)))
            t.Routes.Add(new TaxiRoute { RouteIndex = (short)(i + 1), FromName = "A", ToName = "B", Fare = f, TripType = "OneWay" });
        return t;
    }

    [Fact] public void Complete_taxi_is_done() => Assert.True(TaxiRateRules.Done(Taxi()));
    [Fact] public void Null_rate_is_not_done() => Assert.False(TaxiRateRules.Done(Taxi(rate: null)));
    [Fact] public void Null_km_is_not_done() => Assert.False(TaxiRateRules.Done(Taxi(km: null)));
    [Fact] public void Km_zero_is_not_done() => Assert.False(TaxiRateRules.Done(Taxi(km: 0)));
    [Fact] public void Km_above_1000_is_not_done() => Assert.False(TaxiRateRules.Done(Taxi(km: 1001)));
    [Fact] public void Rate_below_100_is_not_done() => Assert.False(TaxiRateRules.Done(Taxi(rate: 99)));
    [Fact] public void Rate_above_max_is_not_done() => Assert.False(TaxiRateRules.Done(Taxi(rate: TaxiLimits.MaxFare + 1)));
    [Fact] public void No_route_is_not_done() => Assert.False(TaxiRateRules.Done(new TaxiListing
        { DrivingLicenceNumberEnc = "x", DateOfBirth = new DateOnly(1985, 1, 1), LocalDayRate = 1800, LocalDayKm = 80 }));
    [Fact] public void A_route_fare_out_of_range_is_not_done() => Assert.False(TaxiRateRules.Done(Taxi(fares: [1500, 50])));
    [Fact] public void Every_route_fare_in_range_is_done() => Assert.True(TaxiRateRules.Done(Taxi(fares: [1500, 100, TaxiLimits.MaxFare])));

    [Fact] public void Empty_request_asks_for_the_rate()
        => Assert.Equal("Add your day rate", TaxiRateRules.Error(new UpdateTaxiLocalRateRequest()));
    [Fact] public void Rate_without_km_asks_for_the_km()
        => Assert.Equal("Add the km included per day", TaxiRateRules.Error(new UpdateTaxiLocalRateRequest { DayRate = 1800 }));
    [Fact] public void Rate_out_of_range_has_a_message()
        => Assert.Equal(TaxiRateRules.RateRange, TaxiRateRules.Error(new UpdateTaxiLocalRateRequest { DayRate = 99, KmPerDay = 80 }));
    [Theory, InlineData(0), InlineData(1001), InlineData(-1)]
    public void Km_out_of_range_has_a_message(int km)
        => Assert.Equal(TaxiRateRules.KmRange, TaxiRateRules.Error(new UpdateTaxiLocalRateRequest { DayRate = 1800, KmPerDay = km }));
    [Theory, InlineData(100, 1), InlineData(1800, 80), InlineData(TaxiLimits.MaxFare, 1000)]
    public void Valid_request_has_no_error(int rate, int km)
        => Assert.Null(TaxiRateRules.Error(new UpdateTaxiLocalRateRequest { DayRate = rate, KmPerDay = km }));

    [Fact] public void Submit_message_is_plain_english()
        => Assert.Equal("Add your day rate and km before you submit", TaxiRateRules.SubmitMessage);
}
