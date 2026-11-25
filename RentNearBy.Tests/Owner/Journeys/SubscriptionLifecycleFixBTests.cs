using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// Subscription matrix lane BE-FIX-B (B1-B6): L15 races, taxi ways bust, paused card state, paused counter, route save 23505.
public class SubscriptionLifecycleFixBTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    private static string Between(string text, string from, string to)
    {
        var a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, from);
        var b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        return b < 0 ? text[a..] : text[a..b];
    }

    private static string VisibleSrc() => Src("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanVisibleService.cs");

    // B1: Choose what stays decides with the limits read under the listing lock (a grant or expiry before the lock wins).
    [Fact]
    public void Stay_choice_rereads_limits_under_the_lock()
    {
        var stay = Between(VisibleSrc(), "SaveStayAsync(", "SaveTaxiAsync(");
        var lockAt = stay.IndexOf("LockAsync(\"\\\"StayListings\\\"\"", StringComparison.Ordinal);
        var reread = stay.IndexOf("var locked = await entitlements.GetLimitsAsync(userId, ListingKinds.Stay)", StringComparison.Ordinal);
        Assert.True(lockAt > 0 && reread > lockAt);
        Assert.Contains("if (!locked.PaymentEnabled) return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleNotNeeded)", stay);
        Assert.Contains("ChoiceNeeded(photos, locked.Photos)", stay);
        Assert.Contains("ChoiceNeeded(rooms, locked.RoomTypes)", stay);
        Assert.Contains("photoIds, locked.Photos)", stay);
        Assert.Contains("roomIds, locked.RoomTypes)", stay);
        Assert.DoesNotContain("limits.Photos", stay[reread..]);
        Assert.DoesNotContain("limits.RoomTypes", stay[reread..]);
    }

    [Fact]
    public void Taxi_choice_rereads_limits_under_the_lock_and_drops_rolled_back_hints()
    {
        var taxi = Between(VisibleSrc(), "SaveTaxiAsync(", "StayAnswerAsync(");
        var lockAt = taxi.IndexOf("LockAsync(\"\\\"TaxiListings\\\"\"", StringComparison.Ordinal);
        var reread = taxi.IndexOf("var locked = await entitlements.GetLimitsAsync(userId, ListingKinds.Taxi)", StringComparison.Ordinal);
        Assert.True(lockAt > 0 && reread > lockAt);
        var after = taxi[reread..];
        Assert.Contains("!locked.PaymentEnabled || !PlanRowChoice.ChoiceNeeded(routes, locked.Routes)", after);
        Assert.Contains("routeIds, locked.Routes)", after);
        Assert.DoesNotContain("limits.Routes", after);
        // Every refusal after the closure clears its hints (the transaction rolls back without a commit).
        var commit = after.IndexOf("tx.CommitAsync", StringComparison.Ordinal);
        var refusals = System.Text.RegularExpressions.Regex.Matches(after[..commit], @"return (PlanVisibleOutcome\.Fail|new PlanVisibleOutcome)").Count;
        var clears = System.Text.RegularExpressions.Regex.Matches(after[..commit], @"_closed = null;").Count;
        Assert.True(clears >= refusals, $"{clears} clears for {refusals} refusals");
    }

    // B2: the batch takes the owners (KEY SHARE, id order) before the ring locks and the listing locks.
    [Fact]
    public void Batch_locks_users_first()
    {
        Assert.Contains("FROM \"Users\" WHERE \"Id\" = ANY(@users) ORDER BY \"Id\" FOR KEY SHARE", PlanLimitsReconcile.UsersKeyShareSql);
        var batch = Between(Src("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs"),
            "private async Task<(int Changed, int Notified, List<BatchRow> Raced)> ApplyBatchAsync(", "private async Task NotifyAsync(");
        var users = batch.IndexOf("UsersKeyShareSql,", StringComparison.Ordinal);
        Assert.True(users > batch.IndexOf("BeginTransactionAsync", StringComparison.Ordinal));
        Assert.True(users < batch.IndexOf("RingLocksSql", StringComparison.Ordinal));
        Assert.True(users < batch.IndexOf("FOR UPDATE", StringComparison.Ordinal));
        Assert.Contains("rows.Select(r => r.UserId).Distinct().ToArray()", batch);
    }

    // B3: a taxi bust moves the taxi ways network version, also without Redis.
    [Fact]
    public async Task Taxi_bust_bumps_taxi_ways_without_redis()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var bust = new ListingCacheInvalidator(null!, sp, NullLogger<ListingCacheInvalidator>.Instance);

        var before = TaxiWaysVersion.Local;
        await bust.BustDistrictsAsync(ListingKinds.Taxi, [Guid.NewGuid()]);
        Assert.True(TaxiWaysVersion.Local > before);

        before = TaxiWaysVersion.Local;
        await bust.BustForUsersAsync([Guid.NewGuid()], ListingKinds.Taxi);
        Assert.True(TaxiWaysVersion.Local > before);
    }

    [Fact]
    public void Taxi_bust_bumps_before_the_redis_return()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "ListingCacheInvalidator.cs");
        var districts = Between(src, "public async Task BustDistrictsAsync(", "try");
        Assert.True(districts.IndexOf("TaxiWaysVersion.BumpAsync(redis)", StringComparison.Ordinal)
            < districts.IndexOf("if (redis == null) return;", StringComparison.Ordinal));
    }

    // B4: a paused Play subscription with no active row reads On hold, never Ended.
    [Fact]
    public void Paused_with_no_active_row_is_on_hold()
    {
        Assert.Equal(OwnerPlanCardStates.OnHold, OwnerPlanCardStates.Of(true, null, PlayStates.Paused, true, true));
        Assert.Equal(OwnerPlanCardStates.OnHold, OwnerPlanCardStates.Of(true, null, PlayStates.Paused, false, false));
        Assert.Equal(OwnerPlanCardStates.PaymentOff, OwnerPlanCardStates.Of(false, null, PlayStates.Paused, true, true));
    }

    // B5: the paused counter of /plans/me also counts jobs gone inactive on the plan.
    [Fact]
    public void Paused_counter_includes_inactive_jobs()
        => Assert.Contains("dto.PausedByPlanCount = rows.Hidden.Total + rows.InactiveJobs;",
            Src("RentNearBy.Infrastructure", "Services", "PlanPurchaseService.cs"));

    // B6: two route saves racing on a new route index: the loser gets 409, not 500.
    [Fact]
    public void Route_save_unique_violation_is_a_conflict()
    {
        var src = Src("RentNearBy.Api", "Handlers", "TaxiHandlers.cs");
        var block = Between(src, "taxiSignals = await taxiRequests.RoutesChangedAsync(db, taxi.Id, routeKeys);", "await taxiRequests.AfterCommitAsync(taxiSignals");
        var unique = block.IndexOf("SqlState: Npgsql.PostgresErrorCodes.UniqueViolation", StringComparison.Ordinal);
        Assert.True(unique > 0);
        Assert.Contains("return Concurrent();", block[unique..]);
        Assert.True(unique < block.LastIndexOf("throw;", StringComparison.Ordinal));
    }
}
