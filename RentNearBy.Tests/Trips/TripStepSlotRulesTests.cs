using RentNearBy.Api.Handlers;

namespace RentNearBy.Tests.Trips;

public class TripStepSlotRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 6, 0, 0, DateTimeKind.Utc);

    private static TripEnquiry Ask(bool quiet, string status = "Asked", string name = "Stay") => new()
    {
        Id = Guid.NewGuid(), Kind = "Stay", Status = status, ListingId = Guid.NewGuid(), ListingName = name, StepKey = "stay:1",
        CreatedAt = quiet ? Now.AddHours(-13) : Now.AddHours(-1), ExpiresAt = Now.AddDays(2), UpdatedAt = Now,
    };

    private static List<TripEnquiry> Rows(int waiting, int quiet)
        => Enumerable.Range(0, waiting).Select(_ => Ask(false)).Concat(Enumerable.Range(0, quiet).Select(_ => Ask(true))).ToList();

    [Fact]
    public void Caps_are_3_counted_and_6_in_total()
    {
        Assert.Equal(3, TripStepSlotRules.MaxCounted);
        Assert.Equal(6, TripStepSlotRules.MaxTotal);
    }

    [Fact]
    public void Quiet_rows_do_not_count_toward_the_3()
    {
        var s = TripStepSlotRules.Compute(Rows(0, 3), 0, Now);
        Assert.Equal((0, 3, 3), (s.Counted, s.Quiet, s.Free));
        Assert.Null(TripStepSlotRules.FullCase(s, 3, addingIsShortlistRow: false));
        Assert.Equal(3, s.QuietIds.Count);
    }

    [Fact]
    public void Three_waiting_is_the_waiting_case_with_the_next_quiet_time()
    {
        var rows = Rows(3, 0);
        var s = TripStepSlotRules.Compute(rows, 0, Now);
        Assert.Equal(0, s.Free);
        Assert.Equal(TripStepSlotRules.CaseWaiting, TripStepSlotRules.FullCase(s, 1, addingIsShortlistRow: false));
        Assert.Equal(rows.Min(r => TripQuietRules.QuietAt(r.CreatedAt, r.ExpiresAt)), s.NextQuietAt);
    }

    [Fact]
    public void Three_waiting_and_three_quiet_is_the_total_case()
    {
        var s = TripStepSlotRules.Compute(Rows(3, 3), 0, Now);
        Assert.Equal(TripStepSlotRules.CaseTotal, TripStepSlotRules.FullCase(s, 1, addingIsShortlistRow: false));
    }

    [Theory]
    [InlineData(0, 3, 3, null)]       // 3 quiet: 3 more fit
    [InlineData(0, 4, 3, "total")]    // 4 + 3 > 6
    [InlineData(1, 3, 2, null)]
    [InlineData(1, 3, 3, "total")]    // 1 + 3 + 3 = 7
    [InlineData(2, 0, 2, "waiting")]
    [InlineData(2, 1, 1, null)]
    public void A_send_fits_3_counted_and_6_total(int waiting, int quiet, int adding, string? expected)
        => Assert.Equal(expected, TripStepSlotRules.FullCase(TripStepSlotRules.Compute(Rows(waiting, quiet), 0, Now), adding, false));

    [Fact]
    public void Shortlist_rows_share_the_counted_places()
    {
        var s = TripStepSlotRules.Compute(Rows(1, 2), 2, Now);
        Assert.Equal(0, s.Free);
        Assert.Equal(TripStepSlotRules.CaseWaiting, TripStepSlotRules.FullCase(s, 1, addingIsShortlistRow: true));
        // A send turns those Active rows into asks: they are not added twice.
        Assert.Null(TripStepSlotRules.FullCase(s, 2, addingIsShortlistRow: false));
    }

    [Fact]
    public void Free_is_the_smaller_of_both_rules()
    {
        Assert.Equal(3, TripStepSlotRules.Compute([], 0, Now).Free);
        Assert.Equal(2, TripStepSlotRules.Compute(Rows(0, 4), 0, Now).Free);
        Assert.Equal(1, TripStepSlotRules.Compute(Rows(2, 0), 0, Now).Free);
        Assert.Equal(0, TripStepSlotRules.Compute(Rows(1, 5), 0, Now).Free);
    }

    [Fact]
    public void Offered_and_booked_rows_count_and_closed_rows_are_ignored()
    {
        var rows = new List<TripEnquiry> { Ask(true, "Offered"), Ask(true, "ConfirmedByBoth"), Ask(true, "Expired") };
        var s = TripStepSlotRules.Compute(rows, 0, Now);
        Assert.Equal((2, 0), (s.Counted, s.Quiet));
    }

    [Fact]
    public void StepFull_payload_lists_quiet_and_waiting_asks_by_name()
    {
        var quiet = Ask(true, name: "Lakeside Rooms");
        var waiting = Ask(false, name: "Pine Nest");
        var booked = Ask(false, "ConfirmedByBoth", "Booked Stay");
        var rows = new List<TripEnquiry> { quiet, waiting, booked, Ask(true), Ask(true), Ask(false) };
        var slots = TripStepSlotRules.Compute(rows, 0, Now);
        var data = TripGuards.StepFullData(slots, TripStepSlotRules.CaseTotal, rows, Now);

        Assert.Equal("total", data.Case);
        Assert.Equal((3, 3, 0, 3, 6), (data.Counted, data.Quiet, data.Free, data.MaxCounted, data.MaxTotal));
        Assert.Equal(3, data.QuietAsks.Count);
        Assert.Contains(data.QuietAsks, a => a.EnquiryId == quiet.Id && a.ListingName == "Lakeside Rooms" && a.AskedAt == quiet.CreatedAt);
        Assert.Contains(data.WaitingAsks, a => a.EnquiryId == waiting.Id && a.ListingId == waiting.ListingId);
        Assert.DoesNotContain(data.WaitingAsks, a => a.EnquiryId == booked.Id);
    }

    [Fact]
    public void Undo_brings_back_a_quiet_ask_beside_3_counted()
    {
        var s = TripStepSlotRules.Compute(Rows(3, 0), 0, Now);
        var quiet = Ask(true, "Withdrawn");
        Assert.True(TripStepSlotRules.ComesBackQuiet(quiet, offerRestored: false, Now));
        Assert.Null(TripStepSlotRules.RestoreCase(s, comesBackQuiet: true));
        Assert.False(TripStepSlotRules.ComesBackQuiet(quiet, offerRestored: true, Now));
        Assert.Equal(TripStepSlotRules.CaseWaiting, TripStepSlotRules.RestoreCase(s, comesBackQuiet: false));
        Assert.Equal(TripStepSlotRules.CaseTotal, TripStepSlotRules.RestoreCase(TripStepSlotRules.Compute(Rows(3, 3), 0, Now), true));
    }

    [Fact]
    public void StepFull_messages_per_case()
    {
        Assert.Equal(TripBulkRules.StepFullMessage, TripBulkRules.StepFullMessageOf(TripStepSlotRules.CaseWaiting));
        Assert.Equal("This day has 6 asks. Withdraw quiet asks to choose new ones.", TripBulkRules.StepFullMessageOf(TripStepSlotRules.CaseTotal));
    }
}
