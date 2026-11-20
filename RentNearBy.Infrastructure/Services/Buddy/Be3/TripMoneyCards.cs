using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be3.Be3Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be3;

// ITripMoneyCards (contract 3.3). Cards: ONE statement for any number of trips. Standings: canonical order + my shared count
// in one statement, then the ledger's three statements over all the trips.
public sealed class TripMoneyCards(ApplicationDbContext db, ITripLedger ledger) : ITripMoneyCards
{
    private const string CardsSql = """
        SELECT t.id AS "TripId", COALESCE(st."ExpenseCount", 0) AS "ExpenseCount", COALESCE(st."TotalSpent", 0) AS "TotalSpent",
               COALESCE(st."OpenPayments", 0) AS "OpenPayments",
               COALESCE((SELECT sum(s."Share") FROM "TripExpenseShares" s WHERE s."TripId" = t.id AND s."UserId" = @me), 0)::int AS "MyShare"
        FROM unnest(@trips) AS t(id)
        LEFT JOIN "TripExpenseState" st ON st."TripId" = t.id
        """;

    private const string OrderSql = """
        SELECT t."Id" AS "TripId", t."OwnerUserId",
               ARRAY(SELECT m."UserId" FROM "TripMembers" m
                     WHERE m."TripId" = t."Id" AND m."Status" IN ('Joined','Left','Removed')
                     ORDER BY m."JoinedAt" NULLS FIRST, m."UserId") AS "MemberIds",
               (SELECT count(*)::int FROM "TripExpenseShares" s WHERE s."TripId" = t."Id" AND s."UserId" = @me) AS "SharedCount"
        FROM "Trips" t WHERE t."Id" = ANY(@trips)
        """;

    public async Task<IReadOnlyDictionary<Guid, TripMoneyCard>> GetCardsAsync(IReadOnlyCollection<Guid> tripIds, Guid userId, CancellationToken ct = default)
    {
        if (tripIds.Count == 0) return new Dictionary<Guid, TripMoneyCard>();
        var rows = await db.Database.SqlQueryRaw<CardRow>(CardsSql, Uuids("trips", tripIds.Distinct()), Uuid("me", userId)).ToListAsync(ct);
        return rows.ToDictionary(r => r.TripId,
            r => new TripMoneyCard(r.TripId, r.ExpenseCount > 0, r.TotalSpent, r.ExpenseCount, r.MyShare, r.OpenPayments, r.OpenPayments == 0));
    }

    public async Task<IReadOnlyDictionary<Guid, TripMoneyStanding>> GetStandingsAsync(IReadOnlyCollection<Guid> tripIds, Guid userId, CancellationToken ct = default)
    {
        if (tripIds.Count == 0) return new Dictionary<Guid, TripMoneyStanding>();
        var rows = await db.Database.SqlQueryRaw<OrderRow>(OrderSql, Uuids("trips", tripIds.Distinct()), Uuid("me", userId)).ToListAsync(ct);
        if (rows.Count == 0) return new Dictionary<Guid, TripMoneyStanding>();
        var orders = rows.ToDictionary(r => r.TripId, r => (IReadOnlyList<Guid>)[r.OwnerUserId, .. r.MemberIds ?? []]);
        var snapshots = await ledger.LoadManyAsync(orders, ct);
        return rows.ToDictionary(r => r.TripId, r => Standing(r.TripId, snapshots[r.TripId], userId, r.SharedCount));
    }

    internal static TripMoneyStanding Standing(Guid tripId, TripLedgerSnapshot snapshot, Guid userId, int sharedCount)
    {
        var me = snapshot.People.FirstOrDefault(p => p.UserId == userId);
        var owes = snapshot.Open.FirstOrDefault(o => o.FromUserId == userId);
        return new TripMoneyStanding(tripId, me?.SettleNet ?? 0, (me?.Share ?? 0) > 0, sharedCount, owes?.ToUserId);
    }

    private sealed class CardRow
    {
        public Guid TripId { get; set; }
        public int ExpenseCount { get; set; }
        public long TotalSpent { get; set; }
        public int OpenPayments { get; set; }
        public int MyShare { get; set; }
    }

    private sealed class OrderRow
    {
        public Guid TripId { get; set; }
        public Guid OwnerUserId { get; set; }
        public Guid[]? MemberIds { get; set; }
        public int SharedCount { get; set; }
    }
}
