using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy.Be1;

// B1: one statement for every answer (doc 2.0.6 same-response rule).
public sealed class BuddyFindService(ApplicationDbContext db)
{
    internal const string FindSql = $"""
        SELECT u."Id" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username", u."ProfilePhotoUrl" AS "PhotoUrl",
               EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @me AND b."BuddyUserId" = u."Id") AS "IsBuddy",
               r."Id" AS "ReqId", r."Status" AS "ReqStatus", r."CreatedAt" AS "ReqCreatedAt", r."ExpiresAt" AS "ReqExpiresAt",
               r."BlockedSender" AS "ReqBlockedSender", p."Id" AS "TheirPendingId", now() AS "Now"
        FROM "Users" u
        LEFT JOIN LATERAL (
          SELECT x."Id", x."Status", x."CreatedAt", x."ExpiresAt", x."BlockedSender"
          FROM "BuddyRequests" x
          WHERE x."PairLowId" = LEAST(@me, u."Id") AND x."PairHighId" = GREATEST(@me, u."Id") AND x."FromUserId" = @me
          ORDER BY x."CreatedAt" DESC LIMIT 1) r ON TRUE
        LEFT JOIN LATERAL (
          SELECT y."Id"
          FROM "BuddyRequests" y
          WHERE y."PairLowId" = LEAST(@me, u."Id") AND y."PairHighId" = GREATEST(@me, u."Id") AND y."FromUserId" = u."Id"
            AND y."Status" = 'Pending' AND y."ExpiresAt" > now()
          LIMIT 1) p ON TRUE
        WHERE u."Username" = @username AND u."IsActive" AND u."RoleId" = {TravellerOnly.SeekerRoleSql}
          AND NOT EXISTS (SELECT 1 FROM "UserBlocks" ub
                          WHERE (ub."BlockerId" = @me AND ub."BlockedId" = u."Id") OR (ub."BlockerId" = u."Id" AND ub."BlockedId" = @me))
        """;

    public async Task<FindBuddyResponse> FindAsync(Guid callerId, string? query, CancellationToken ct = default)
    {
        var username = BuddyRules.NormalizeFind(query) ?? BuddyRules.ImpossibleUsername;
        var rows = await db.Database.SqlQueryRaw<FindRow>(FindSql, Be1Sql.Uuid("me", callerId), Be1Sql.Text("username", username))
            .ToListAsync(ct);
        if (rows.Count == 0) return new FindBuddyResponse { Found = false };
        var r = rows[0];
        var mine = r.ReqId is { } id
            ? new RequestFacts(id, callerId, r.ReqStatus!, r.ReqCreatedAt!.Value, r.ReqExpiresAt!.Value, r.ReqBlockedSender ?? false)
            : null;
        var relation = BuddyRules.Relation(callerId, r.UserId, r.IsBuddy, mine, r.TheirPendingId, r.Now);
        return new FindBuddyResponse
        {
            Found = true,
            Person = new FoundPersonDto
            {
                UserId = r.UserId, Name = r.Name, Username = r.Username, Initial = MoneyPeople.Initial(r.Name),
                PhotoUrl = CloudinaryThumb.Apply(r.PhotoUrl, CloudinaryThumb.PersonFace),
                Relation = relation.Relation, RequestId = relation.RequestId, ExpiresAt = relation.ExpiresAt,
            },
        };
    }

    private sealed class FindRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string? PhotoUrl { get; set; }
        public bool IsBuddy { get; set; }
        public Guid? ReqId { get; set; }
        public string? ReqStatus { get; set; }
        public DateTime? ReqCreatedAt { get; set; }
        public DateTime? ReqExpiresAt { get; set; }
        public bool? ReqBlockedSender { get; set; }
        public Guid? TheirPendingId { get; set; }
        public DateTime Now { get; set; }
    }
}
