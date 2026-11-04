using System.Text.Json;

namespace RentNearBy.Tests.Trips;

public class TripLeadRulesTests
{
    private static TripEnquiryContext Ctx(object value) =>
        JsonSerializer.Deserialize<TripEnquiryContext>(JsonSerializer.Serialize(value, TripJson.Options), TripJson.Options)!;

    private static object Day(int day, string date, int minutes, params string[] spots) => new { day, date, minutes, spots };

    private static TripEnquiryContext Full(string? pickupTime = "08:30", string? pickupPlace = "Mallital") => Ctx(new
    {
        city = "Nainital",
        adults = 2,
        kids = 1,
        travellerName = "Rahul",
        pickupTime,
        pickupPlace,
        days = new[]
        {
            Day(1, "2026-11-01", 335, "Mall Road", "Naini Lake"),
            Day(2, "2026-11-02", 420, "Snow View", "Tiffin Top"),
        },
    });

    private static readonly DateOnly In = new(2026, 11, 1);
    private static readonly DateOnly Out = new(2026, 11, 3);

    [Fact]
    public void Constants_match_the_contract()
    {
        Assert.Equal(3, TripLeadRules.MaxFareEdits);
        Assert.Equal(5, TripLeadRules.LeadsPerTripPerDay);
        Assert.Equal(3, TripLeadRules.MaxOpenLeadsPerDay);
        Assert.Equal(7, TripLeadRules.PhoneVisibleAfterCloseDays);
        Assert.Equal(24, TripLeadRules.AnsweredWithinHours);
    }

    [Theory]
    [InlineData(1800, 2, 3600)]
    [InlineData(1800, 1, 1800)]
    [InlineData(1800, 0, 1800)]
    [InlineData(1800, -3, 1800)]
    [InlineData(null, 3, 0)]
    [InlineData(0, 3, 0)]
    public void Listed_total(int? rate, int days, int expected) => Assert.Equal(expected, TripLeadRules.ListedTotal(rate, days));

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    public void Edits_left(short count, int left) => Assert.Equal(left, TripLeadRules.EditsLeft(count));

    [Fact]
    public void Lead_text_has_trip_dates_people_pickup_and_every_day()
    {
        var text = TripEnquiryText.BuildLead(Full(), In, Out);
        Assert.StartsWith("Trip to Nainital", text);
        Assert.Contains("01 Nov to 02 Nov", text);
        Assert.DoesNotContain("03 Nov", text);
        Assert.Contains("2 adults, 1 kid", text);
        Assert.Contains("8:30 am", text);
        Assert.Contains("Mallital", text);
        Assert.Contains("Day 1", text);
        Assert.Contains("5 h 35 min", text);
        Assert.Contains("Mall Road, Naini Lake", text);
        Assert.Contains("Day 2", text);
        Assert.Contains("7 h", text);
        Assert.Contains("Snow View, Tiffin Top", text);
    }

    [Fact]
    public void Lead_text_omits_the_pickup_sentence_when_both_are_missing()
    {
        var text = TripEnquiryText.BuildLead(Full(null, null), In, Out);
        Assert.DoesNotContain("Pickup", text);
        Assert.Contains("Day 1", text);
    }

    [Fact]
    public void Lead_text_keeps_pickup_when_only_the_place_is_given()
    {
        var text = TripEnquiryText.BuildLead(Full(null, "Mallital"), In, Out);
        Assert.Contains("Mallital", text);
    }

    [Fact]
    public void Lead_text_cuts_long_spot_lists()
    {
        var spots = Enumerable.Range(1, 20).Select(i => $"Spot{i}").ToArray();
        var ctx = Ctx(new { city = "Nainital", adults = 2, kids = 0, days = new[] { Day(1, "2026-11-01", 300, spots) } });
        var text = TripEnquiryText.BuildLead(ctx, In, Out);
        Assert.Contains("Spot1", text);
        Assert.DoesNotContain("Spot20", text);
        Assert.Contains("more", text);
    }

    [Fact]
    public void Lead_text_never_claims_verification_or_payment()
    {
        var text = TripEnquiryText.BuildLead(Full(), In, Out).ToLowerInvariant();
        Assert.DoesNotContain("verified", text);
        Assert.DoesNotContain("guaranteed", text);
        Assert.DoesNotContain("advance", text);
    }

    [Theory]
    [InlineData(0, 0, "new")]
    [InlineData(2, 2, "new")]
    [InlineData(3, 0, "phone")]
    [InlineData(3, 1, "phone")]
    [InlineData(3, 2, "app")]
    [InlineData(4, 2, "app")]
    [InlineData(4, 1, "phone")]
    [InlineData(10, 5, "app")]
    [InlineData(10, 4, "phone")]
    public void Honest_reply_label_thresholds(int received, int answered, string expected)
        => Assert.Equal(expected, TaxiTripReplyLabels.Of(received, answered));

    [Fact]
    public void Reply_label_values_are_the_app_wire_values()
    {
        Assert.Equal("new", TaxiTripReplyLabels.New);
        Assert.Equal("app", TaxiTripReplyLabels.App);
        Assert.Equal("phone", TaxiTripReplyLabels.Phone);
    }
}
