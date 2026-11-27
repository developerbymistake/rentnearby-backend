using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Core.Models.Social;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;

namespace RentNearBy.Tests.Stays.Quotation;

// Q14 / D-3 / ARCHITECTURE 4.3 with a fixed server clock: the same document is free for 24 h (5 free uses per window, same number),
// the window never slides on a free use, after it the same number is counted again, free even while over the limit, and the IST
// month restarts the count on the 1st with no job.
public class QuoteReserveWindowTests(TestDatabase db) : IntegrationTestBase(db)
{
    private static readonly DateTime Start = new(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc);

    private async Task<RentNearBy.Infrastructure.Services.Owner.Quote.QuoteResult<RentNearBy.Core.DTOs.Owner.Quote.QuoteReserveDto>> At(
        QuoteClock clock, Guid user, Guid doc, short kind = QuoteKinds.QuotationCode)
    {
        await using var c = CreateContext();
        return await QuoteKit.Reserve(c, clock).ReserveAsync(user, doc, kind, default);
    }

    [IntegrationFact]
    public async Task Free_until_the_window_ends_then_counted_with_the_same_number()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        var clock = new QuoteClock(Start);
        var doc = Guid.NewGuid();
        var first = (await At(clock, user, doc)).Value!;
        Assert.True(first.Counted);

        clock.Set(Start.AddHours(23).AddMinutes(59));
        var free = (await At(clock, user, doc)).Value!;
        Assert.Equal((first.Number, false, 1, QuoteRules.MaxFreePerWindow - 1), (free.Number, free.Counted, free.Usage.Used, free.FreeLeft));
        Assert.Equal(first.FreeUntil, free.FreeUntil);

        clock.Set(Start.AddHours(24).AddMinutes(1));
        var again = (await At(clock, user, doc)).Value!;
        Assert.Equal((first.Number, true, 2, QuoteRules.MaxFreePerWindow), (again.Number, again.Counted, again.Usage.Used, again.FreeLeft));
        Assert.Equal(clock.UtcNow + QuoteRules.FreeWindow, again.FreeUntil);
        Assert.Equal(first.IssuedAt, again.IssuedAt);
    }

    [IntegrationFact]
    public async Task Sixth_download_in_one_window_is_counted_or_refused_at_the_cap()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using (var c = CreateContext()) await QuoteKit.PlanAsync(c, user, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(20));
        var clock = new QuoteClock(Start);
        var doc = Guid.NewGuid();
        Assert.True((await At(clock, user, doc)).Value!.Counted);
        for (var i = 0; i < QuoteRules.MaxFreePerWindow; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.False((await At(clock, user, doc)).Value!.Counted);
        }
        clock.Advance(TimeSpan.FromMinutes(10));
        var sixth = (await At(clock, user, doc)).Value!;
        Assert.Equal((true, 2), (sixth.Counted, sixth.Usage.Used));

        // Free plan at its cap: the sixth is refused instead.
        var free = await QuoteKit.ReadyOwnerAsync(CreateContext);
        var d2 = Guid.NewGuid();
        Assert.True((await At(clock, free, Guid.NewGuid())).Ok);
        Assert.True((await At(clock, free, d2)).Ok);
        for (var i = 0; i < QuoteRules.MaxFreePerWindow; i++) Assert.True((await At(clock, free, d2)).Ok);
        Assert.Equal(QuoteErrors.MonthLimit, (await At(clock, free, d2)).ErrorType);
    }

    [IntegrationFact]
    public async Task Window_does_not_slide_on_a_free_use()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        var clock = new QuoteClock(Start);
        var doc = Guid.NewGuid();
        await At(clock, user, doc);
        clock.Set(Start.AddHours(20));
        await At(clock, user, doc);
        clock.Set(Start.AddHours(24).AddMinutes(1));
        Assert.True((await At(clock, user, doc)).Value!.Counted);
        await using var c = CreateContext();
        var issue = await c.StayQuoteIssues.SingleAsync(i => i.UserId == user && i.DocumentId == doc);
        Assert.Equal((2, 0), ((int)issue.Downloads, (int)issue.FreeUses));
    }

    [IntegrationFact]
    public async Task Free_re_download_works_while_over_the_limit()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        var clock = new QuoteClock(Start);
        var doc = Guid.NewGuid();
        await At(clock, user, doc);
        await At(clock, user, Guid.NewGuid());
        Assert.Equal(QuoteErrors.MonthLimit, (await At(clock, user, Guid.NewGuid())).ErrorType);
        var free = (await At(clock, user, doc)).Value!;
        Assert.Equal((false, 2), (free.Counted, free.Usage.Used));
    }

    // IST month: 30 Nov 23:59 IST and 1 Dec 00:00 IST are different months; the count restarts inside the UPDATE.
    [IntegrationFact]
    public async Task Month_rolls_over_at_ist_midnight_on_the_first()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        var lastMinute = IstTime.IstMidnightUtc(new DateOnly(2026, 12, 1)).AddMinutes(-1);
        var clock = new QuoteClock(lastMinute);
        await At(clock, user, Guid.NewGuid());
        await At(clock, user, Guid.NewGuid());
        Assert.Equal(QuoteErrors.MonthLimit, (await At(clock, user, Guid.NewGuid())).ErrorType);

        clock.Set(lastMinute.AddMinutes(1));
        var december = (await At(clock, user, Guid.NewGuid())).Value!;
        Assert.Equal((true, 1), (december.Counted, december.Usage.Used));
        Assert.Equal(SocialMonth.ResetsAtUtc(clock.UtcNow), december.Usage.ResetsAt);
        Assert.Equal(3, december.Number);
        await using var c = CreateContext();
        Assert.Equal(SocialMonth.Key(new DateOnly(2026, 12, 1)), (await c.StayQuoteCounters.SingleAsync(x => x.UserId == user)).MonthKey);
    }

    // M-1: a request that read the clock in the old month and waited on the lock behind a request of the new month counts in the
    // stored (newer) month; the month key never moves backwards and no count is lost at the edge.
    [IntegrationFact]
    public async Task Month_key_never_moves_backwards_at_the_ist_edge()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        var midnight = IstTime.IstMidnightUtc(new DateOnly(2026, 12, 1));
        var clock = new QuoteClock(midnight.AddSeconds(30));
        Assert.Equal(1, (await At(clock, user, Guid.NewGuid())).Value!.Usage.Used);

        clock.Set(midnight.AddSeconds(-1));
        var late = (await At(clock, user, Guid.NewGuid())).Value!;
        Assert.Equal((true, 2), (late.Counted, late.Usage.Used));

        await using (var c = CreateContext())
        {
            var counter = await c.StayQuoteCounters.AsNoTracking().SingleAsync(x => x.UserId == user);
            Assert.Equal((SocialMonth.Key(new DateOnly(2026, 12, 1)), 2), (counter.MonthKey, (int)counter.Used));
        }

        clock.Set(midnight.AddMinutes(1));
        var refused = await At(clock, user, Guid.NewGuid());
        Assert.Equal(QuoteErrors.MonthLimit, refused.ErrorType);
    }
}
