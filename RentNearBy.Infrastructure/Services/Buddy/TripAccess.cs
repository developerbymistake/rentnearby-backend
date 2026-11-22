using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Utils;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy;

// First working version (foundation); from the Build phase this file belongs to BE2. One statement per call.
public sealed class TripAccess(ApplicationDbContext db) : ITripAccess
{
    internal const string AccessSql = """
        SELECT t."Id" AS "TripId", t."OwnerUserId", COALESCE(u."Name", '') AS "OwnerName", t."Title", t."Status",
               t."StartDate", t."EndDate", t."DayCount", t."SpotCount", t."CompletedAt", t."UpdatedAt",
               m."Id" AS "MemberId", m."Status" AS "MemberStatus", m."JoinedAt"
        FROM "Trips" t
        JOIN "Users" u ON u."Id" = t."OwnerUserId"
        LEFT JOIN "TripMembers" m ON m."TripId" = t."Id" AND m."UserId" = @user
        WHERE t."Id" = @trip
        """;

    // Owner first, then JoinedAt ascending, then UserId ascending (uuid order = lowercase text order).
    internal const string TravellersSql = """
        SELECT x."TripId", x."UserId", x."Name", x."Username", x."IsOwner", x."Status", x."JoinedAt", x."PhotoUrl"
        FROM (
          SELECT t."Id" AS "TripId", t."OwnerUserId" AS "UserId", COALESCE(u."Name", '') AS "Name", u."Username",
                 TRUE AS "IsOwner", 'Owner'::varchar AS "Status", NULL::timestamptz AS "JoinedAt", u."ProfilePhotoUrl" AS "PhotoUrl"
          FROM "Trips" t JOIN "Users" u ON u."Id" = t."OwnerUserId"
          WHERE t."Id" = ANY(@trips)
          UNION ALL
          SELECT m."TripId", m."UserId", COALESCE(u."Name", ''), u."Username", FALSE, m."Status"::varchar, m."JoinedAt", u."ProfilePhotoUrl"
          FROM "TripMembers" m JOIN "Users" u ON u."Id" = m."UserId"
          WHERE m."TripId" = ANY(@trips) AND m."Status" = ANY(@statuses)
        ) x
        ORDER BY x."TripId", x."IsOwner" DESC, x."JoinedAt" NULLS FIRST, x."UserId"
        """;

    private static readonly string[] JoinedOnly = [TripMemberStatuses.Joined];
    private static readonly string[] WithFormer = [TripMemberStatuses.Joined, TripMemberStatuses.Left, TripMemberStatuses.Removed];

    public async Task<TripAccessInfo?> GetAsync(Guid tripId, Guid userId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<AccessRow>(AccessSql,
            new NpgsqlParameter("trip", NpgsqlDbType.Uuid) { Value = tripId },
            new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId }).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var r = rows[0];
        var level = TripAccessRules.Level(r.OwnerUserId, userId, r.MemberStatus);
        if (level is null) return null;
        var isOwner = level == TripAccessLevel.Owner;
        return new TripAccessInfo(
            r.TripId, r.OwnerUserId, r.OwnerName, r.Title, r.Status, r.StartDate, r.EndDate, r.DayCount, r.SpotCount,
            r.CompletedAt, r.UpdatedAt, level.Value,
            isOwner ? null : r.MemberId,
            isOwner ? null : r.JoinedAt,
            level == TripAccessLevel.Former ? r.MemberStatus : null);
    }

    public async Task<IReadOnlyList<TripTraveller>> GetTravellersAsync(Guid tripId, bool includeFormer, CancellationToken ct = default)
    {
        var rows = await ReadAsync([tripId], includeFormer ? WithFormer : JoinedOnly, ct);
        return rows.Select(ToTraveller).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TripTraveller>>> GetTravellersManyAsync(
        IReadOnlyCollection<Guid> tripIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, IReadOnlyList<TripTraveller>>();
        if (tripIds.Count == 0) return result;
        var rows = await ReadAsync(tripIds.Distinct().ToArray(), WithFormer, ct);
        foreach (var group in rows.GroupBy(r => r.TripId))
            result[group.Key] = group.Select(ToTraveller).ToList();
        return result;
    }

    private Task<List<TravellerRow>> ReadAsync(Guid[] tripIds, string[] statuses, CancellationToken ct)
        => db.Database.SqlQueryRaw<TravellerRow>(TravellersSql,
            new NpgsqlParameter("trips", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = tripIds },
            new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = statuses }).ToListAsync(ct);

    private static TripTraveller ToTraveller(TravellerRow r)
        => new(r.UserId, r.Name, r.Username, r.IsOwner, r.Status, r.JoinedAt, CloudinaryThumb.Apply(r.PhotoUrl, CloudinaryThumb.PersonFace));

    private sealed class AccessRow
    {
        public Guid TripId { get; set; }
        public Guid OwnerUserId { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        public int DayCount { get; set; }
        public int SpotCount { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public Guid? MemberId { get; set; }
        public string? MemberStatus { get; set; }
        public DateTime? JoinedAt { get; set; }
    }

    private sealed class TravellerRow
    {
        public Guid TripId { get; set; }
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Username { get; set; }
        public bool IsOwner { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? JoinedAt { get; set; }
        public string? PhotoUrl { get; set; }
    }
}
