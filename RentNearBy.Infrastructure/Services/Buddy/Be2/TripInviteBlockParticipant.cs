using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// Inside the block transaction, after the pair lock: waiting invites between the two (both directions) become Cancelled.
// A block never removes a Joined traveller (doc 3.0.12). Silent: no notice, no push.
public sealed class TripInviteBlockParticipant(ApplicationDbContext db) : IUserBlockParticipant
{
    private const string AffectedTripsSql = """
        SELECT m."TripId" AS "Value"
        FROM "TripMembers" m JOIN "Trips" t ON t."Id" = m."TripId"
        WHERE m."Status" = 'Invited'
          AND ((m."UserId" = @a AND t."OwnerUserId" = @b) OR (m."UserId" = @b AND t."OwnerUserId" = @a))
        """;

    private const string CancelSql = """
        UPDATE "TripMembers" m SET "Status" = 'Cancelled', "EndedAt" = now(), "UpdatedAt" = now(), "Version" = m."Version" + 1
        FROM "Trips" t
        WHERE t."Id" = m."TripId" AND m."TripId" = ANY(@trips) AND m."Status" = 'Invited'
          AND ((m."UserId" = @a AND t."OwnerUserId" = @b) OR (m."UserId" = @b AND t."OwnerUserId" = @a))
        RETURNING m."UserId", m."TripId"
        """;

    public async Task OnBlockedAsync(Guid blockerId, Guid blockedId, CancellationToken ct = default)
    {
        var trips = await db.Database.SqlQueryRaw<Guid>(AffectedTripsSql, Id("a", blockerId), Id("b", blockedId)).ToListAsync(ct);
        if (trips.Count == 0) return;
        await BuddyLocks.LockTripsAsync(db, trips, ct);
        var cancelled = await db.Database.SqlQueryRaw<PairRow>(CancelSql,
            Ids("trips", trips.Distinct()), Id("a", blockerId), Id("b", blockedId)).ToListAsync(ct);
        await DeleteInviteNoticesAsync(db, cancelled.Select(c => (c.UserId, c.TripId)).ToList(), ct);
    }

    private sealed class PairRow
    {
        public Guid UserId { get; set; }
        public Guid TripId { get; set; }
    }
}
