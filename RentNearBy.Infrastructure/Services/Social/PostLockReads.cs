using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Infrastructure.Services.Social.SocialSql;

namespace RentNearBy.Infrastructure.Services.Social;

// Delete rule (PostLockRules): the rule fields of one trip and its other joined travellers (the owner has no member row).
// The delete path holds the trip row lock (TripHandlers.Delete: TripGuards.LockTripAsync; lockRow: true takes it here), and
// every join (accept, invite accept) takes the same row lock, so the count cannot change under the check.
public static class PostLockReads
{
    internal static readonly string ReadSql = $"""
        SELECT t."IsPublic", t."PublishedAt", t."LastPublishedAt", t."Status", t."StartDate", t."EndDate", t."PostWindowMonths",
               (SELECT count(*)::int FROM "TripMembers" m WHERE m."TripId" = t."Id" AND m."Status" = '{TripMemberStatuses.Joined}') AS "OtherMembers"
        FROM "Trips" t
        WHERE t."Id" = @trip AND (@owner::uuid IS NULL OR t."OwnerUserId" = @owner::uuid)
        """;

    internal const string LockSql = """
        SELECT 1 AS "Value" FROM "Trips" WHERE "Id" = @trip AND (@owner::uuid IS NULL OR "OwnerUserId" = @owner::uuid) FOR UPDATE
        """;

    // ownerId null = any owner (admin path). null = no such trip.
    public static async Task<(PostLockState State, int OtherMembers)?> ReadAsync(ApplicationDbContext db, Guid tripId, Guid? ownerId, bool lockRow,
        CancellationToken ct = default)
    {
        if (lockRow && (await db.Database.SqlQueryRaw<int>(LockSql, Uuid("trip", tripId), NullUuid("owner", ownerId)).ToListAsync(ct)).Count == 0)
            return null;
        var row = (await db.Database.SqlQueryRaw<Row>(ReadSql, Uuid("trip", tripId), NullUuid("owner", ownerId)).ToListAsync(ct)).FirstOrDefault();
        if (row is null) return null;
        return (new PostLockState(row.IsPublic, Utc(row.PublishedAt), Utc(row.LastPublishedAt), row.Status, row.StartDate, row.EndDate, row.PostWindowMonths),
            row.OtherMembers);
    }

    private static DateTime? Utc(DateTime? at) => at is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private sealed class Row
    {
        public bool IsPublic { get; set; }
        public DateTime? PublishedAt { get; set; }
        public DateTime? LastPublishedAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public short? PostWindowMonths { get; set; }
        public int OtherMembers { get; set; }
    }
}
