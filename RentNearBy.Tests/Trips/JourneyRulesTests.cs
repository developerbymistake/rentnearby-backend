namespace RentNearBy.Tests.Trips;

public class JourneyRulesTests
{
    private static TripEnquiry E(string status, int? driverFare = null, int? agreed = null) =>
        new() { Status = status, DriverFare = driverFare, AgreedFare = agreed };

    [Theory]
    [InlineData(TripEnquiryStatuses.FareSent)]
    [InlineData(TripEnquiryStatuses.BookedByTraveller)]
    [InlineData(TripEnquiryStatuses.ConfirmedByBoth)]
    public void Answered_leads_are_free(string status) =>
        Assert.Equal(JourneyLegStatuses.Free, JourneyRules.LegStatusOf(E(status, 5000)));

    [Theory]
    [InlineData(TripEnquiryStatuses.NotAvailable)]
    [InlineData(TripEnquiryStatuses.Failed)]
    public void Denied_leads_are_not_free(string status) =>
        Assert.Equal(JourneyLegStatuses.NotFree, JourneyRules.LegStatusOf(E(status)));

    [Fact]
    public void Asked_is_no_answer() =>
        Assert.Equal(JourneyLegStatuses.NoAnswer, JourneyRules.LegStatusOf(E(TripEnquiryStatuses.Asked)));

    [Theory]
    [InlineData(TripEnquiryStatuses.Expired)]
    [InlineData(TripEnquiryStatuses.Closed)]
    [InlineData(TripEnquiryStatuses.CancelledByTraveller)]
    [InlineData(TripEnquiryStatuses.CancelledByOwner)]
    public void Ended_leads_are_unknown(string status) =>
        Assert.Equal(JourneyLegStatuses.Unknown, JourneyRules.LegStatusOf(E(status)));

    [Fact]
    public void Limits_match_the_contract()
    {
        Assert.Equal(20, JourneyRules.MaxLive);
        Assert.Equal(3, JourneyRules.MaxLegs);
        Assert.Equal(2, JourneyRules.Backups);
        Assert.Equal(30, JourneyRules.RefreshPerWindow);
    }

    [Fact]
    public void Dedupe_keys_fit_the_push_log_column()
    {
        var j = Guid.NewGuid();
        var t = Guid.NewGuid();
        Assert.True(JourneyRules.PausedKey(j, t).Length <= 80);
        Assert.True(JourneyRules.BackKey(j, t, new DateOnly(2026, 11, 1)).Length <= 80);
        Assert.True(JourneyRules.EveKey(j).Length <= 80);
    }

    [Fact]
    public void Dedupe_keys_are_distinct_per_event()
    {
        var j = Guid.NewGuid();
        var t = Guid.NewGuid();
        Assert.NotEqual(JourneyRules.PausedKey(j, t), JourneyRules.BackKey(j, t, new DateOnly(2026, 11, 1)));
        Assert.NotEqual(JourneyRules.BackKey(j, t, new DateOnly(2026, 11, 1)), JourneyRules.BackKey(j, t, new DateOnly(2026, 11, 2)));
        Assert.Equal(JourneyRules.PausedKey(j, t), JourneyRules.PausedKey(j, t));
    }
}
