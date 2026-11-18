using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;

namespace RentNearBy.Tests.TaxiRequests;

// Contract section 6, missed rings rule (P26).
public class RingMissRulesTests
{
    private static readonly DateTime Acked = new(2026, 12, 10, 9, 41, 9, DateTimeKind.Utc);

    [Fact]
    public void Unacked_timeout_counts_one_miss() => Assert.True(RingMissRules.CountsAsMiss(null, false));

    [Fact]
    public void Acked_but_unanswered_timeout_does_not_count() => Assert.False(RingMissRules.CountsAsMiss(Acked, false));

    [Fact]
    public void Late_or_not_shown_ack_counts_as_a_miss()
    {
        // The ack stores PushLate = late or not shown (contract 7).
        Assert.True(RingMissRules.CountsAsMiss(Acked, true));
        Assert.True(RingMissRules.CountsAsMiss(null, true));
    }

    [Fact]
    public void Third_miss_writes_the_notice_once()
    {
        int?[] streaks = [1, 2, 3, 4, 5, null];
        Assert.Equal([false, false, true, false, false, false], streaks.Select(RingMissRules.NoticeDue).ToArray());
        Assert.Equal(1, streaks.Count(RingMissRules.NoticeDue));
        Assert.Equal(3, TaxiDispatchRules.RingMissLimit);

        // The handler adds the miss through the store helper and writes the notice only on the value it returned.
        var handler = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "DueHandlers", "OfferExpireHandler.cs");
        var count = TaxiSource.At(handler, "RingMissRules.CountsAsMiss(offer.PushReceivedAt, offer.PushLate)");
        var add = TaxiSource.At(handler, "store.AddRingMissAsync(");
        var due = TaxiSource.At(handler, "RingMissRules.NoticeDue(streak)");
        var notice = TaxiSource.At(handler, "TaxiNoticeKind.RingsMissed");
        var move = TaxiSource.At(handler, "OfferStates.Missed, OfferEndReasons.Expired");
        Assert.True(move < count && count < add && add < due && due < notice);
    }

    [Fact]
    public void Reaching_the_limit_bumps_the_pickup_lists_after_commit()
    {
        var handler = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "DueHandlers", "OfferExpireHandler.cs");
        Assert.Contains("_leftPool = RingMissRules.NoticeDue(streak);", handler);
        var handle = TaxiSource.Member(handler, "public async Task<DueOutcome> HandleAsync(");
        Assert.DoesNotContain("places.BumpAsync", handle);
        var after = TaxiSource.Member(handler, "public async Task AfterCommitAsync(");
        Assert.Contains("if (_leftPool) await places.BumpAsync();", after);
    }

    [Fact]
    public void Accept_and_decline_reset_the_counter()
    {
        var service = TaxiSource.Read("RentNearBy.Infrastructure", "Services", "TaxiRequests", "TaxiOfferService.cs");
        var accept = TaxiSource.Member(service, "private async Task<TaxiActionResult> AcceptUnitAsync(");
        // The answer proves the ring arrived on every path: counted, missed (full) and closed (driver no longer eligible).
        Assert.Equal(3, CountOf(accept, "store.ResetRingMissAsync(db, offer.DriverListingId, ct)"));
        var decline = TaxiSource.Member(service, "private async Task<TaxiActionResult> DeclineUnitAsync(");
        var ringing = TaxiSource.At(decline, "case OfferStates.Ringing:");
        var selected = TaxiSource.At(decline, "case OfferStates.Selected:");
        var reset = TaxiSource.At(decline, "store.ResetRingMissAsync(db, offer.DriverListingId, ct)");
        Assert.True(ringing < reset && reset < selected);
    }

    private static int CountOf(string text, string marker)
    {
        var n = 0;
        for (var i = text.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
