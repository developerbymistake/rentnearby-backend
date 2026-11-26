using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Buddy.Be2.Be2Sql;

namespace RentNearBy.Infrastructure.Services.Buddy.Be2;

// Trip limit (CONTRACT_TRIPLIMIT 1, social contract 2.4): active trips = owned Draft/Planned + joined on trips not Completed;
// one statement gives the live count and the list. The window count is taken before LIMIT. A trip kept inactive after Plus
// ended (PLAN_STATES_SOCIAL 1) holds no slot.
public static class TripSlots
{
    internal const string SlotsSql = $"""
        SELECT x."Id", x."Title", x."Role", x."StartDate", x."EndDate", x."OwnerName", count(*) OVER ()::int AS "Total"
        FROM (
          SELECT t."Id", t."Title", '{TripSharingRoles.Owner}'::text AS "Role", t."StartDate", t."EndDate", NULL::text AS "OwnerName", t."UpdatedAt" AS "At"
          FROM "Trips" t WHERE t."OwnerUserId" = @me AND t."Status" IN ('{TripStatuses.Draft}', '{TripStatuses.Planned}') AND NOT t."InactiveByPlan"
          UNION ALL
          SELECT t."Id", t."Title", '{TripSharingRoles.Viewer}'::text, t."StartDate", t."EndDate", COALESCE(o."Name", ''), m."JoinedAt"
          FROM "TripMembers" m
          JOIN "Trips" t ON t."Id" = m."TripId"
          JOIN "Users" o ON o."Id" = t."OwnerUserId"
          WHERE m."UserId" = @me AND m."Status" = '{TripMemberStatuses.Joined}' AND t."Status" <> '{TripStatuses.Completed}'
        ) x
        ORDER BY x."EndDate" ASC NULLS LAST, x."At" ASC, x."Id"
        LIMIT @limit
        """;

    // limit = the caller's plan value (ISeekerLimits.ActiveTrips); the list holds at most that many trips.
    public static async Task<TripSlotsDto> ReadAsync(ApplicationDbContext db, Guid userId, int limit, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<SlotRow>(SlotsSql, Id("me", userId), Int("limit", Math.Max(1, limit))).ToListAsync(ct);
        return new TripSlotsDto
        {
            Limit = limit,
            Count = rows.Count == 0 ? 0 : rows[0].Total,
            Trips = rows.Select(r => new TripSlotTripDto
            {
                Id = r.Id, Title = r.Title, Role = r.Role, StartDate = r.StartDate, EndDate = r.EndDate, OwnerName = r.OwnerName,
            }).ToList(),
        };
    }

    // Inside the caller's transaction: slot lock first (lock order step 0), then the count. Null = a slot is free.
    public static async Task<TripSlotsDto?> RefusalUnderLockAsync(ApplicationDbContext db, Guid userId, int limit, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Trip slot check needs an open transaction.");
        await BuddyLocks.LockTripSlotsAsync(db, userId, ct);
        var slots = await ReadAsync(db, userId, limit, ct);
        return TripSlotRules.IsFull(slots.Count, limit) ? slots : null;
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
