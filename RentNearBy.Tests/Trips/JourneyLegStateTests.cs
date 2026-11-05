namespace RentNearBy.Tests.Trips;

public class JourneyLegStateTests
{
    private static readonly DateTime Created = new(2026, 11, 5, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Replied = new(2026, 11, 5, 5, 0, 0, DateTimeKind.Utc);

    private static TripEnquiry Lead(string status) => new()
    {
        Id = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Taxi,
        Status = status,
        DriverFare = 3500,
        CreatedAt = Created,
        UpdatedAt = Replied,
        DriverRepliedAt = status == TripEnquiryStatuses.Asked ? null : Replied,
    };

    [Fact]
    public void Fare_sent_shows_free_with_the_fare()
    {
        var leg = new SavedJourneyLeg { LastKnownStatus = JourneyLegStatuses.NoAnswer };
        JourneyLegState.Of(Lead(TripEnquiryStatuses.FareSent)).ApplyTo(leg);
        Assert.Equal(JourneyLegStatuses.Free, leg.LastKnownStatus);
        Assert.Equal(3500, leg.LastQuotedFare);
        Assert.Equal(Replied, leg.LastKnownAt);
    }

    [Fact]
    public void Not_available_shows_not_free_without_a_fare()
    {
        var s = JourneyLegState.Of(Lead(TripEnquiryStatuses.NotAvailable));
        Assert.Equal(JourneyLegStatuses.NotFree, s.Status);
        Assert.Null(s.QuotedFare);
    }

    [Fact]
    public void Lapsed_reply_keeps_the_fare()
    {
        var s = JourneyLegState.Of(Lead(TripEnquiryStatuses.Expired));
        Assert.Equal(JourneyLegStatuses.Free, s.Status);
        Assert.Equal(3500, s.QuotedFare);
    }

    [Fact]
    public void Expired_without_a_reply_is_unknown()
    {
        var e = Lead(TripEnquiryStatuses.Expired);
        e.DriverRepliedAt = null;
        e.DriverFare = null;
        Assert.Equal(JourneyLegStatuses.Unknown, JourneyLegState.Of(e).Status);
    }

    [Fact]
    public void Asked_shows_no_answer_since_the_ask()
    {
        var s = JourneyLegState.Of(Lead(TripEnquiryStatuses.Asked));
        Assert.Equal(JourneyLegStatuses.NoAnswer, s.Status);
        Assert.Equal(Created, s.KnownAt);
    }
}
