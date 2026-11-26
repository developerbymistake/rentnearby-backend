using System.Text.RegularExpressions;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Services.Buddy.Be2;

namespace RentNearBy.Tests.Buddy.Be2;

public class TripLimitRulesTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void IsFull_at_and_above_the_limit(int count, bool full) => Assert.Equal(full, TripSlotRules.IsFull(count, 3));

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(9, 10, false)]
    [InlineData(10, 10, true)]
    public void IsFull_uses_the_plan_limit(int count, int limit, bool full) => Assert.Equal(full, TripSlotRules.IsFull(count, limit));

    [Fact]
    public void Limit_is_three() => Assert.Equal(3, TripLimits.MaxTripsPerUser);

    [Fact]
    public void TripLimitReached_is_409_and_message_carries_the_constant()
    {
        Assert.Equal(409, BuddyErrorTypes.StatusOf(BuddyErrorTypes.TripLimitReached));
        Assert.Equal($"You can keep {TripLimits.MaxTripsPerUser} trips. Delete one to make room.",
            BuddyErrorTypes.MessageOf(BuddyErrorTypes.TripLimitReached));
    }

    [Fact]
    public void Slot_lock_key_is_per_user() =>
        Assert.Equal("trip:slots:11111111-1111-4111-8111-111111111111",
            BuddyPairs.TripSlotsLockKey(Guid.Parse("11111111-1111-4111-8111-111111111111")));

    [Fact]
    public void Slots_sql_takes_the_limit_as_a_parameter_and_counts_joined_only()
    {
        var sql = TripSlots.SlotsSql;
        Assert.Contains("LIMIT @limit", sql);
        Assert.DoesNotMatch(new Regex(@"\b3\b"), sql);
        Assert.Contains("count(*) OVER ()", sql);
        Assert.Contains("m.\"Status\" = 'Joined'", sql);
        Assert.Contains("'Owner'::text", sql);
        Assert.Contains("'Viewer'::text", sql);
        // Active trips only (social contract 2.4): owned Draft/Planned, joined on trips not Completed.
        Assert.Contains("t.\"Status\" IN ('Draft', 'Planned')", sql);
        Assert.Contains("t.\"Status\" <> 'Completed'", sql);
    }

    private static string Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RentNearBy.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    [Fact]
    public void Accept_takes_the_slot_lock_before_the_trip_lock_and_checks_after_TripFull()
    {
        var src = Source("RentNearBy.Infrastructure", "Services", "Buddy", "Be2", "TripInviteService.cs");
        var accept = src[src.IndexOf("AcceptAsync(Guid memberId", StringComparison.Ordinal)..];
        accept = accept[..accept.IndexOf("DeclineAsync(Guid memberId", StringComparison.Ordinal)];
        var slot = accept.IndexOf("LockTripSlotsAsync(db, callerId", StringComparison.Ordinal);
        var trip = accept.IndexOf("LockTripAsync(db, first.TripId", StringComparison.Ordinal);
        var full = accept.IndexOf("BuddyErrorTypes.TripFull", StringComparison.Ordinal);
        var limit = accept.IndexOf("BuddyErrorTypes.TripLimitReached", StringComparison.Ordinal);
        var write = accept.IndexOf("AcceptSql", StringComparison.Ordinal);
        Assert.True(slot > 0 && slot < trip, "slot lock must come before the trip lock");
        Assert.True(full < limit && limit < write, "TripFull, then TripLimitReached, then the write");
        Assert.Matches(new Regex(@"BeginTransactionAsync\(c\);\s*await BuddyLocks\.LockTripSlotsAsync\(db, callerId, c\);"), accept);
    }

    [Fact]
    public void Create_checks_the_limit_inside_the_insert_transaction()
    {
        var src = Source("RentNearBy.Api", "Handlers", "TripHandlers.cs");
        var create = src[src.IndexOf("public static async Task<IResult> Create(", StringComparison.Ordinal)..];
        create = create[..create.IndexOf("public static Trip NewTrip(", StringComparison.Ordinal)];
        var begin = create.IndexOf("BeginTransactionAsync()", StringComparison.Ordinal);
        var check = create.IndexOf("TripSlots.RefusalUnderLockAsync(db, userId, activeTripLimit)", StringComparison.Ordinal);
        var add = create.IndexOf("Trips.AddAsync(trip)", StringComparison.Ordinal);
        var commit = create.IndexOf("CommitTransactionAsync()", StringComparison.Ordinal);
        Assert.True(begin > 0 && begin < check && check < add && add < commit);
        Assert.Contains("BuddyErrorTypes.TripLimitReached, full)", create);
    }
}
