using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// Trip limit (CONTRACT_TRIPLIMIT 1): one statement gives the live count and the list. The window count is taken before LIMIT.
public static class TripSlots
{
    internal const string SlotsSql = $"""
        SELECT x."Id", x."Title", x."Role", x."StartDate", x."EndDate", x."OwnerName", count(*) OVER ()::int AS "Total"
        FROM (
          SELECT t."Id", t."Title", '{TripSharingRoles.Owner}'::text AS "Role", t."StartDate", t."EndDate", NULL::text AS "OwnerName", t."UpdatedAt" AS "At"
          FROM "Trips" t WHERE t."OwnerUserId" = @me
          UNION ALL
          SELECT t."Id", t."Title", '{TripSharingRoles.Viewer}'::text, t."StartDate", t."EndDate", COALESCE(o."Name", ''), m."JoinedAt"
          FROM "TripMembers" m
          JOIN "Trips" t ON t."Id" = m."TripId"
          JOIN "Users" o ON o."Id" = t."OwnerUserId"
          WHERE m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Joined}'
        ) x
        ORDER BY x."EndDate" ASC NULLS LAST, x."At" ASC, x."Id"
        LIMIT @limit
        """;

    public static async Task<TripSlotsDto> ReadAsync(ApplicationDbContext db, Guid userId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<SlotRow>(SlotsSql, Id("me", userId), Int("limit", TripLimits.MaxTripsPerUser)).ToListAsync(ct);
        return new TripSlotsDto
        {
            Limit = TripLimits.MaxTripsPerUser,
            Count = rows.Count == 0 ? 0 : rows[0].Total,
            Trips = rows.Select(r => new TripSlotTripDto
            {
                Id = r.Id, Title = r.Title, Role = r.Role, StartDate = r.StartDate, EndDate = r.EndDate, OwnerName = r.OwnerName,
            }).ToList(),
        };
    }

    // Inside the caller's transaction: slot lock first (lock order step 0), then the count. Null = a slot is free.
    public static async Task<TripSlotsDto?> RefusalUnderLockAsync(ApplicationDbContext db, Guid userId, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Trip slot check needs an open transaction.");
        await BuddyLocks.LockTripSlotsAsync(db, userId, ct);
        var slots = await ReadAsync(db, userId, ct);
        return TripSlotRules.IsFull(slots.Count) ? slots : null;
    }

    private sealed class SlotRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public string? OwnerName { get; set; }
        public int Total { get; set; }
    }
}
