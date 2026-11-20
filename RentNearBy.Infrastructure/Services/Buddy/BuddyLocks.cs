using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy;

// Contract 5.1 lock order: sender -> pair(s) -> trip row(s) -> rows. Transaction-scoped; call inside the caller's transaction.
public static class BuddyLocks
{
    public static Task LockSenderAsync(ApplicationDbContext db, Guid userId, CancellationToken ct = default)
        => LockKeysAsync(db, [BuddyPairs.SenderLockKey(userId)], ct);

    public static Task LockPairAsync(ApplicationDbContext db, Guid a, Guid b, CancellationToken ct = default)
        => LockKeysAsync(db, [BuddyPairs.PairLockKey(a, b)], ct);

    // One statement, keys in ascending order.
    public static Task LockPairsAsync(ApplicationDbContext db, Guid me, IEnumerable<Guid> others, CancellationToken ct = default)
        => LockKeysAsync(db, others.Where(o => o != me).Select(o => BuddyPairs.PairLockKey(me, o)), ct);

    public static Task LockTripAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct = default)
        => db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Trips\" WHERE \"Id\" = {tripId} FOR UPDATE", ct);

    // Owner routes: the trip row is locked only for its owner; false = not his trip (or gone), nothing locked.
    public static async Task<bool> LockOwnedTripAsync(ApplicationDbContext db, Guid tripId, Guid ownerId, CancellationToken ct = default)
        => (await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM \"Trips\" WHERE \"Id\" = {tripId} AND \"OwnerUserId\" = {ownerId} FOR UPDATE")
            .ToListAsync(ct)).Count > 0;

    public static Task LockTripsAsync(ApplicationDbContext db, IEnumerable<Guid> tripIds, CancellationToken ct = default)
    {
        var ids = tripIds.Distinct().ToArray();
        if (ids.Length == 0) return Task.CompletedTask;
        if (ids.Length == 1) return LockTripAsync(db, ids[0], ct);
        return db.Database.ExecuteSqlRawAsync(
            "SELECT 1 FROM \"Trips\" WHERE \"Id\" = ANY(@ids) ORDER BY \"Id\" FOR UPDATE",
            [new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids }], ct);
    }

    private static Task LockKeysAsync(ApplicationDbContext db, IEnumerable<string> keys, CancellationToken ct)
    {
        var sorted = keys.Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
        if (sorted.Length == 0) return Task.CompletedTask;
        return db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(k, 0)) FROM unnest(@keys) WITH ORDINALITY AS u(k, n) ORDER BY n",
            [new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = sorted }], ct);
    }
}
