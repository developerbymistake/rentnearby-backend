using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueHandlers;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Integration.Journeys;

namespace RentNearBy.Tests.TaxiRequests;

// Security review of phase B2: lost wait timer, taken down drivers, ring versus select race.
public class TaxiRequestReviewFixTests
{
    private static string Infra(params string[] parts) => TaxiSource.Read(["RentNearBy.Infrastructure", "Services", "TaxiRequests", .. parts]);

    [Fact]
    public void A_wait_counts_only_until_its_ten_minutes_are_over()
    {
        var now = new DateTime(2026, 12, 10, 9, 0, 0, DateTimeKind.Utc);
        Assert.False(MatcherSql.WaitRunning(null, now));
        Assert.True(MatcherSql.WaitRunning(now.AddSeconds(-(TaxiDispatchRules.QueueWaitSeconds - 1)), now));
        Assert.False(MatcherSql.WaitRunning(now.AddSeconds(-TaxiDispatchRules.QueueWaitSeconds), now));
        Assert.False(MatcherSql.WaitRunning(now.AddDays(-2), now));
    }

    [Fact]
    public void Start_wait_restarts_a_wait_that_is_over_and_the_wait_branch_uses_the_same_rule()
    {
        Assert.Contains($"(\"QueueWaitStartedAt\" IS NULL OR \"QueueWaitStartedAt\" + interval '{TaxiDispatchRules.QueueWaitSeconds} seconds' <= now())", MatcherSql.StartWait);
        Assert.Contains("\"WaitRound\" = \"WaitRound\" + 1", MatcherSql.StartWait);

        var apply = TaxiSource.Member(Infra("Matching", "PostgresMatcherPassUnit.cs"), "public async Task<MatcherApplyResult> ApplyAsync(");
        Assert.Contains("if (MatcherSql.WaitRunning(row.QueueWaitStartedAt, data.Now)) break;", apply);
        Assert.DoesNotContain("if (row.QueueWaitStartedAt is not null) break;", apply);
    }

    [Fact]
    public void Every_path_back_to_asking_clears_the_wait()
    {
        Assert.Contains("\"QueueWaitStartedAt\" = NULL", TaxiRequestStoreSql.Reopen(RequestStates.Selected, RequestStates.Open));
        Assert.Contains("\"QueueWaitStartedAt\" = NULL", TaxiRequestService.AskMoreSql);
        Assert.Contains("\"QueueWaitStartedAt\" = NULL", MatcherSql.MarkServed);
    }

    [Fact]
    public void A_skipped_wait_timer_ends_only_its_own_round()
    {
        var sql = RequestWaitExpireHandler.EndRoundSql;
        Assert.Contains("SET \"QueueWaitStartedAt\" = NULL", sql);
        Assert.Contains("\"WaitRound\" = @round", sql);
        Assert.Contains($"\"QueueWaitStartedAt\" + interval '{TaxiDispatchRules.QueueWaitSeconds} seconds' <= now()", sql);
        Assert.DoesNotContain("\"Version\"", sql);

        var handle = TaxiSource.Member(Infra("DueHandlers", "RequestWaitExpireHandler.cs"), "public async Task<DueOutcome> HandleAsync(");
        var live = TaxiSource.At(handle, "if (live.Ringing > 0 || live.Accepted > 0 || live.Selected > 0)");
        var end = TaxiSource.At(handle, "EndRoundSql");
        var end2 = TaxiSource.At(handle, "item.RoundNo");
        Assert.True(end > live && end2 > live && end < TaxiSource.At(handle, "store.EndRequestAsync("), "The live-driver skip ends the round before returning");
    }

    [Fact]
    public void Bookable_driver_sql_matches_the_confirm_rule()
    {
        Assert.Contains("NOT l.\"IsDeleted\"", TaxiOfferRows.DriverBookableSql);
        Assert.Contains($"l.\"Status\" = '{TaxiStatuses.Approved}'", TaxiOfferRows.DriverBookableSql);
        Assert.DoesNotContain("FOR UPDATE", TaxiOfferRows.DriverBookableSql);
        Assert.True(new DriverLockRow(Guid.NewGuid(), Guid.NewGuid(), TaxiStatuses.Approved, false).IsBookable);
        Assert.False(new DriverLockRow(Guid.NewGuid(), Guid.NewGuid(), TaxiStatuses.Rejected, false).IsBookable);
        Assert.False(new DriverLockRow(Guid.NewGuid(), Guid.NewGuid(), TaxiStatuses.PendingReview, false).IsBookable);
    }

    [Fact]
    public void Accept_closes_the_ring_of_a_driver_who_can_no_longer_take_it_before_counting()
    {
        var source = Infra("TaxiOfferService.cs");
        var accept = TaxiSource.Member(source, "private async Task<TaxiActionResult> AcceptUnitAsync(");
        var block = TaxiSource.At(accept, "await AcceptBlockAsync(request, offer.DriverListingId, ct);");
        Assert.True(block > TaxiSource.At(accept, "var (request, offer) = await LockAsync(offerId, ct);"));
        Assert.True(block < TaxiSource.At(accept, "AcceptCountSql"), "A blocked ring is never counted as an accept");

        var check = TaxiSource.Member(source, "private async Task<(string? CloseReason, TaxiActionResult? Refusal)> AcceptBlockAsync(");
        Assert.Contains("TaxiOfferRows.IsDriverBookableAsync(db, driverListingId, ct)", check);
        Assert.Contains("OfferEndReasons.ListingRemoved", check);
        Assert.Contains("DriverCalendar.ConflictForAsync(db, driverListingId, request.DateFrom, request.DateTo, ct)", check);
        Assert.Contains("OfferEndReasons.Booked", check);

        // Contract D3: the only refusal for a ring that cannot count is OfferMissed.
        Assert.Equal(TaxiRequestErrorTypes.OfferMissed, TaxiOfferService.ClosedRingRefusal.Error);
        Assert.True(TaxiRequestTransitions.CanMove(TransitionKinds.Offer, OfferStates.Ringing, OfferStates.Closed));
    }

    [Fact]
    public void Select_refuses_a_taken_down_driver_and_takes_the_ring_lock_before_selecting()
    {
        var select = TaxiSource.Member(Infra("TaxiRequestService.cs"), "public async Task<TaxiRequestResult> SelectAsync(");
        var bookable = TaxiSource.At(select, "TaxiOfferRows.IsDriverBookableAsync(db, offer.DriverListingId, c)");
        var ringLock = TaxiSource.At(select, "MatcherSql.LockDriverRing");
        var move = TaxiSource.At(select, "OfferStates.Selected, null, null, c)");
        Assert.True(bookable < move && ringLock < move, "Both checks run before the offer becomes Selected");
        Assert.Contains("OfferStates.Closed, OfferEndReasons.ListingRemoved", select);
        Assert.Contains("Step.OfferClosed(", select);
        Assert.True(TaxiRequestTransitions.CanMove(TransitionKinds.Offer, OfferStates.Accepted, OfferStates.Closed));

        // A refused step that still changed rows is committed and signalled before the error is answered.
        var run = TaxiSource.Member(Infra("TaxiRequestService.cs"), "private async Task<TaxiRequestResult> RunAsync(");
        Assert.True(TaxiSource.At(run, "await store.AfterCommitAsync(signals);") < TaxiSource.At(run, "if (outcome.Error is { } error)"));
    }

    [Fact]
    public void Ring_lock_key_is_shared_and_the_ring_insert_skips_a_driver_with_a_pending_selection()
    {
        const string key = "hashtextextended('taxi-ring:' ||";
        Assert.Contains(key, MatcherSql.TryLockDrivers);
        Assert.Contains(key, MatcherSql.LockDriverRing);
        Assert.Contains("pg_advisory_xact_lock(", MatcherSql.LockDriverRing);
        Assert.Contains($"WHERE NOT EXISTS (SELECT 1 FROM \"TaxiRequestOffers\" s WHERE s.\"DriverListingId\" = @driverId AND s.\"Status\" = '{OfferStates.Selected}')", MatcherSql.InsertOffer);
        Assert.True(MatcherSql.InsertOffer.IndexOf("WHERE NOT EXISTS", StringComparison.Ordinal) < MatcherSql.InsertOffer.IndexOf("ON CONFLICT", StringComparison.Ordinal));
    }

    [Fact]
    public void App_registration_gives_one_taxi_closure_for_both_types()
    {
        var source = TaxiSource.Read("RentNearBy.Api", "Extensions", "TaxiRequestServiceCollectionExtensions.cs");
        Assert.Contains("services.AddScoped<TaxiRequestClosure>();", source);
        Assert.Contains("services.AddScoped<ITaxiRequestClosure>(sp => sp.GetRequiredService<TaxiRequestClosure>());", source);
    }
}

// Written for a real PostgreSQL (RENTNEARBY_TEST_DB); skipped without one.
[Trait("Category", "Integration")]
public class TaxiRequestReviewFixIntegrationTests(TestDatabase db) : IntegrationTestBase(db)
{
    [IntegrationFact]
    public async Task Accept_by_a_taken_down_driver_closes_the_ring_and_counts_nothing()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 1);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1);
        await using (var s = h.NewScope())
            await s.Db.TaxiListings.Where(t => t.Id == seed.Drivers[0].Listing).ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, TaxiStatuses.Rejected));

        await using (var s = h.NewScope())
            Assert.Equal(TaxiRequestErrorTypes.OfferMissed, (await TaxiOfferKit.For(s.Db).Offers.AcceptAsync(offer, seed.Drivers[0].User)).Error);

        await using var read = CreateContext();
        var row = await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == offer);
        Assert.Equal((OfferStates.Closed, OfferEndReasons.ListingRemoved), (row.Status, row.EndReason));
        var request = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
        Assert.Equal((RequestStates.Open, 0), (request.Status, (int)request.AcceptedCount));
        Assert.False(await read.Set<DueAction>().AnyAsync(d => d.SubjectId == offer && d.Kind == DueActionKinds.OfferExpire && d.DoneAt == null));
    }

    [IntegrationFact]
    public async Task Accept_by_a_driver_booked_on_those_dates_closes_the_ring()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Open, 1);
        var other = await TaxiOfferKit.SeedAsync(h, RequestStates.Booked, 0);
        var confirmed = await TaxiOfferKit.AddOfferAsync(h, other.RequestId, seed.Drivers[0], OfferStates.Confirmed, 1);
        await TaxiOfferKit.AddBookingAsync(h, other, confirmed, seed.Drivers[0], BookingStates.Booked, seed.From, seed.To);
        var offer = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Ringing, 1);

        await using (var s = h.NewScope())
            Assert.Equal(TaxiRequestErrorTypes.OfferMissed, (await TaxiOfferKit.For(s.Db).Offers.AcceptAsync(offer, seed.Drivers[0].User)).Error);

        await using var read = CreateContext();
        var row = await read.Set<TaxiRequestOffer>().AsNoTracking().SingleAsync(o => o.Id == offer);
        Assert.Equal((OfferStates.Closed, OfferEndReasons.Booked), (row.Status, row.EndReason));
        Assert.Equal(0, (await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId)).AcceptedCount);
    }

    // Wait, timer skipped because of a live accept, accept gone and request reopened, wait again: a new round with its own timer.
    [IntegrationFact]
    public async Task Wait_skip_reopen_and_wait_again_starts_a_new_round()
    {
        await using var h = new JourneyHarness(Db);
        var seed = await TaxiOfferKit.SeedAsync(h, RequestStates.Collecting, 1, accepted: 1);
        var accepted = await TaxiOfferKit.AddOfferAsync(h, seed.RequestId, seed.Drivers[0], OfferStates.Accepted, 1);
        await using (var s = h.NewScope())
            await s.Db.Set<TaxiRequest>().Where(r => r.Id == seed.RequestId).ExecuteUpdateAsync(u => u
                .SetProperty(r => r.QueueWaitStartedAt, DateTime.UtcNow.AddMinutes(-11)).SetProperty(r => r.WaitRound, 1));

        await using (var s = h.NewScope())
        {
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            var kit = TaxiOfferKit.For(s.Db);
            var item = new DueActionItem(Guid.NewGuid(), DueActionKinds.RequestWaitExpire, seed.RequestId, 1, seed.RequestId, DateTime.UtcNow, 0);
            Assert.Equal(DueOutcome.Skipped, await new RequestWaitExpireHandler(kit.Store).HandleAsync(s.Db, item, CancellationToken.None));
            await tx.CommitAsync();
        }
        await using (var read = CreateContext())
        {
            var afterSkip = await read.Set<TaxiRequest>().AsNoTracking().SingleAsync(r => r.Id == seed.RequestId);
            Assert.Null(afterSkip.QueueWaitStartedAt);
            Assert.Equal(RequestStates.Collecting, afterSkip.Status);
        }

        await using (var s = h.NewScope())
            Assert.True((await TaxiOfferKit.For(s.Db).Offers.WithdrawAsync(accepted, seed.Drivers[0].User, ReasonChips.All[0])).Ok);

        await using (var s = h.NewScope())
        {
            await using var tx = await s.Db.Database.BeginTransactionAsync();
            var rows = await s.Db.Database.SqlQueryRaw<WaitRow>(MatcherSql.StartWait,
                new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = seed.RequestId }).ToListAsync();
            await tx.CommitAsync();
            var row = Assert.Single(rows);
            Assert.Equal(2, row.WaitRound);
            Assert.NotNull(row.QueueWaitStartedAt);
        }
    }

    private sealed class WaitRow
    {
        public long Version { get; set; }
        public string Status { get; set; } = "";
        public int WaitRound { get; set; }
        public DateTime? QueueWaitStartedAt { get; set; }
    }
}
