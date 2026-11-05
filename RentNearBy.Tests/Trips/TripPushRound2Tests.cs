using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Trips;

public class TripPushRound2Tests
{
    private static readonly DateOnly Start = new(2026, 11, 5);
    private static readonly Guid Owner = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Traveller = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static TripEnquiry Stay(DateOnly checkIn, DateOnly checkOut) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        TravellerUserId = Traveller,
        OwnerUserId = Owner,
        ListingId = Guid.NewGuid(),
        ListingName = "Deodar Homestay",
        CheckIn = checkIn,
        CheckOut = checkOut,
        ConversationId = Guid.NewGuid(),
        ContextJson = """{"adults":2}""",
        Status = TripEnquiryStatuses.Asked,
    };

    // Finding 1: a day-count change alone keeps open rows; only a new start closes them.
    [Fact]
    public void Same_start_with_fewer_days_does_not_close_rows()
    {
        Assert.False(TripDateRules.StartMoved(Start, Start));
        Assert.True(TripDateRules.StartMoved(Start, Start.AddDays(1)));
        Assert.False(TripDateRules.StartMoved(null, Start));
    }

    // Finding 6: stay dates read as nights.
    [Fact]
    public void Stay_push_dates_read_as_nights()
    {
        Assert.Equal("Night of 05 Nov", TripNotifications.Dates(Stay(Start, Start.AddDays(1))));
        Assert.Equal("2 nights from 05 Nov", TripNotifications.Dates(Stay(Start, Start.AddDays(2))));
        var n = TripNotifications.TripEnquiryReceived(Stay(Start, Start.AddDays(1)), new TripPushContext(Owner, "Asha", "Deodar Homestay", null));
        Assert.Contains("Night of 05 Nov", n.Body);
        Assert.DoesNotContain("05-06 Nov", n.Body);
    }

    // Finding 5: the offer push quotes the offer's own dates.
    [Fact]
    public void Offer_push_uses_the_offer_dates()
    {
        var e = Stay(Start, Start.AddDays(2));
        var ctx = new TripPushContext(Traveller, "Asha", "Deodar Homestay", null, 3500, CheckIn: Start, CheckOut: Start.AddDays(1));
        var n = TripNotifications.TripOfferReceived(e, ctx);
        Assert.Contains("Rs 3,500 for Night of 05 Nov", n.Body);
    }

    // Finding 17: the stay Problem push opens the failed row in Enquiries, not the shared chat.
    [Fact]
    public void Stay_problem_push_opens_the_failed_row()
    {
        var failed = Stay(Start.AddDays(1), Start.AddDays(3));
        var n = TripNotifications.TripProblemReported(failed, new TripPushContext(Owner, "Asha", "Deodar Homestay", """{"conversationId":"x"}"""));
        Assert.Equal(UserNotificationRoutes.StayEnquiries, n.ActionRoute);
        Assert.Contains(failed.Id.ToString(), n.ActionArgumentsJson);
    }

    // Finding 13: explicit nights win over allNights and stop at the last night.
    [Theory]
    [InlineData(1, false, null, 2)]
    [InlineData(1, true, null, 4)]
    [InlineData(1, true, 2, 3)]
    [InlineData(2, false, 9, 4)]
    [InlineData(3, false, 1, 4)]
    public void Stay_check_out_follows_nights(int day, bool allNights, int? nights, int expectedDay)
    {
        var checkOut = TripDateRules.StayCheckOut(Start, 3, day, allNights, nights);
        Assert.Equal(TripExecutionRules.NightDate(Start, expectedDay), checkOut);
    }

    [Fact]
    public void Nights_out_of_range_is_rejected()
    {
        var v = new CreateTripEnquiryRequestValidator();
        var ok = new CreateTripEnquiryRequest { Day = 1, ListingIds = [Guid.NewGuid()], Nights = 2 };
        Assert.True(v.Validate(ok).IsValid);
        ok.Nights = 0;
        Assert.False(v.Validate(ok).IsValid);
        ok.Nights = TripLimits.MaxDays + 1;
        Assert.False(v.Validate(ok).IsValid);
        ok.Nights = null;
        Assert.True(v.Validate(ok).IsValid);
    }

    // Finding 8: a counter keeps the host's rooms and lines.
    [Fact]
    public void Counter_keeps_the_offer_units()
    {
        var lines = TripOfferRules.CounterLines(
            [new TripOfferLine { RoomName = "Deluxe", Units = 2, Guests = 4, PricePerNight = 3000 }], 2500, 1, 4);
        var line = Assert.Single(lines);
        Assert.Equal(2, line.Units);
        Assert.Equal("Deluxe", line.RoomName);
        Assert.Equal(2500, line.PricePerNight);
        Assert.Equal(10000, TripExecutionRules.ComputeTotal(lines, 2));
    }

    [Fact]
    public void Counter_keeps_every_room_type()
    {
        var lines = TripOfferRules.CounterLines(
        [
            new TripOfferLine { RoomName = "Deluxe", Units = 1, Guests = 2, PricePerNight = 3000 },
            new TripOfferLine { RoomName = "Standard", Units = 1, Guests = 2, PricePerNight = 2000 },
        ], 2000, 1, 4);
        Assert.Equal(2, lines.Count);
        Assert.Equal(2400, lines[0].PricePerNight);
        Assert.Equal(1600, lines[1].PricePerNight);
    }

    [Fact]
    public void Counter_without_an_offer_uses_the_rooms()
    {
        var line = Assert.Single(TripOfferRules.CounterLines(null, 2500, 2, 4));
        Assert.Equal(2, line.Units);
        Assert.Equal(2500, line.PricePerNight);
    }

    // Finding 20: a replaced driver keeps his other days at a prorated fare.
    [Fact]
    public void Replacing_one_day_keeps_the_other_days()
    {
        Assert.Equal(new List<int> { 1, 3 }, TripLeadRules.RemainingDays([1, 2, 3], [2]));
        Assert.Empty(TripLeadRules.RemainingDays([2], [2]));
        Assert.Equal(4000, TripLeadRules.ProrateFare(6000, 2, 3));
        Assert.Null(TripLeadRules.ProrateFare(null, 2, 3));
    }
}
