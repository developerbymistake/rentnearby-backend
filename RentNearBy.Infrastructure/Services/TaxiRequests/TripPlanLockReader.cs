using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// The active taxi request of a trip (at most one, ux_taxirequests_active_trip). Static readers so trip handlers keep their
// signatures; the instance form serves ITripPlanLockReader. Call after the trip row lock when the answer guards a write.
public sealed class TripPlanLockReader : ITripPlanLockReader
{
    // The IN list repeats the index filter word for word, so the partial unique index answers it.
    public static readonly string ActiveRequestSql =
        $"SELECT \"Id\", \"Status\" FROM \"TaxiRequests\" WHERE \"TripId\" = @tripId AND \"Status\" IN ({RequestStates.ActiveSqlList}) LIMIT 1";

    public Task<TripPlanLockState> GetAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct = default) => ReadAsync(db, tripId, ct);

    public static async Task<TripPlanLockState> ReadAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct = default)
    {
        var row = await ActiveAsync(db, tripId, ct);
        return row != null && PlanLockReasons.For(row.Status) is { } reason
            ? new TripPlanLockState(true, reason, row.Id)
            : TripPlanLockState.Unlocked;
    }

    public static async Task<bool> HasActiveRequestAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct = default)
        => await ActiveAsync(db, tripId, ct) != null;

    private static async Task<ActiveRequestRow?> ActiveAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct)
        => (await db.Database
            .SqlQueryRaw<ActiveRequestRow>(ActiveRequestSql, new NpgsqlParameter("tripId", NpgsqlDbType.Uuid) { Value = tripId })
            .ToListAsync(ct))
            .FirstOrDefault();

    private sealed class ActiveRequestRow
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
