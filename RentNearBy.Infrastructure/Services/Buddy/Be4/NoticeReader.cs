using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy.Be4;

// N1, N2 and the counts of the old inbox routes. Trip title and actor name are read live; nothing stored is text.
public sealed class NoticeReader(ApplicationDbContext db)
{
    // One statement: the page, then live trip title, actor name, expense title and traveller count joined to it (the count is
    // one grouped read over the page's TripJoined trips).
    private static string PageSql(string keyset) => $"""
        WITH p AS (
          SELECT n.* FROM "UserNotices" n
          WHERE n."UserId" = @user{keyset}
          ORDER BY n."UpdatedAt" DESC, n."Id" DESC
          LIMIT @take),
        j AS (
          SELECT m."TripId", count(*)::int AS "Joined" FROM "TripMembers" m
          WHERE m."Status" = '{TripMemberStatuses.Joined}'
            AND m."TripId" IN (SELECT p."TripId" FROM p WHERE p."Kind" = '{NoticeKinds.TripJoined}')
          GROUP BY m."TripId")
        SELECT n."Id", n."Kind", n."TripId", t."Title" AS "TripTitle", n."ActorUserId", a."Name" AS "ActorName", n."RefId", n."Amount",
               n."Count", n."CreatedAt", n."UpdatedAt", n."ReadAt",
               COALESCE(e."Title", f."Name",
                        CASE WHEN n."Kind" = '{NoticeKinds.StayEditApproved}' THEN s."Name"
                             WHEN n."Kind" = '{NoticeKinds.StayEditRejected}' THEN s."PendingEditRejectReason" END) AS "RefTitle",
               f."DateFrom" AS "RefDateFrom", f."DateTo" AS "RefDateTo", me."RoleId" AS "ViewerRoleId",
               CASE WHEN n."Kind" = '{NoticeKinds.TripJoined}' AND t."Id" IS NOT NULL THEN 1 + COALESCE(j."Joined", 0) END AS "Travellers"
        FROM p n
        LEFT JOIN "Trips" t ON t."Id" = n."TripId"
        LEFT JOIN "Users" a ON a."Id" = n."ActorUserId"
        LEFT JOIN "Users" me ON me."Id" = @user
        LEFT JOIN "TripExpenses" e ON e."Id" = n."RefId" AND e."TripId" = n."TripId" AND e."DeletedAt" IS NULL
          AND n."Kind" IN ('{NoticeKinds.ExpenseAdded}', '{NoticeKinds.ShareChanged}')
        LEFT JOIN "FestivalDates" f ON f."Id" = n."RefId" AND n."Kind" = '{NoticeKinds.FestivalReminder}'
        LEFT JOIN "StayListings" s ON s."Id" = n."RefId" AND n."Kind" IN ('{NoticeKinds.StayEditApproved}', '{NoticeKinds.StayEditRejected}')
        LEFT JOIN j ON j."TripId" = n."TripId"
        ORDER BY n."UpdatedAt" DESC, n."Id" DESC
        """;

    internal static readonly string FirstPageSql = PageSql(string.Empty);

    internal static readonly string NextPageSql = PageSql("""

            AND (n."UpdatedAt", n."Id") < (@at, @id)
        """.TrimEnd());

    internal const string UnreadSql = """
        SELECT count(*)::int AS "Value" FROM "UserNotices" WHERE "UserId" = @user AND "ReadAt" IS NULL
        """;

    internal const string MarkReadSql = """
        UPDATE "UserNotices" SET "ReadAt" = now() WHERE "Id" = @id AND "UserId" = @user AND "ReadAt" IS NULL
        """;

    internal const string MarkAllReadSql = """
        UPDATE "UserNotices" SET "ReadAt" = now() WHERE "UserId" = @user AND "ReadAt" IS NULL
        """;

    internal const string RemindedTodaySql = """
        SELECT "UserId" AS "Value" FROM "UserNotices"
        WHERE "TripId" = @trip AND "Kind" = 'SettleReminder' AND "UpdatedAt" >= @since
        """;

    // 2 statements: page, unread count.
    public async Task<NoticePageDto> PageAsync(Guid userId, (DateTime At, Guid Id)? after, int limit, CancellationToken ct = default)
    {
        var user = User(userId);
        var take = new NpgsqlParameter("take", NpgsqlDbType.Integer) { Value = limit + 1 };
        var rows = after is { } k
            ? await db.Database.SqlQueryRaw<Row>(NextPageSql, user, take,
                new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(k.At, DateTimeKind.Utc) },
                new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = k.Id }).ToListAsync(ct)
            : await db.Database.SqlQueryRaw<Row>(FirstPageSql, user, take).ToListAsync(ct);
        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).Select(ToDto).ToList();
        var unread = await UnreadCountAsync(userId, ct);
        return new NoticePageDto
        {
            Items = page,
            NextCursor = KeysetCursor.EncodeLast(page, hasMore, i => i.UpdatedAt, i => i.Id),
            Unread = unread,
        };
    }

    public async Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<int>(UnreadSql, User(userId)).ToListAsync(ct)).FirstOrDefault();

    public Task MarkReadAsync(Guid id, Guid userId, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(MarkReadSql, [new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id }, User(userId)], ct);

    public Task MarkAllReadAsync(Guid userId, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(MarkAllReadSql, [User(userId)], ct);

    // Payers of the trip whose SettleReminder was written since the given instant (start of the IST day).
    public async Task<IReadOnlySet<Guid>> RemindedSinceAsync(Guid tripId, DateTime sinceUtc, CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<Guid>(RemindedTodaySql,
                new NpgsqlParameter("trip", NpgsqlDbType.Uuid) { Value = tripId },
                new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc) })
            .ToListAsync(ct)).ToHashSet();

    public static NoticeItemDto ToDto(Row r)
    {
        var route = NoticeRoutes.For(r.Kind, r.TripId, r.RefId, ViewerKind(r.ViewerRoleId));
        if (r.Kind == NoticeKinds.FestivalReminder && r.RefDateFrom is { } from && r.RefDateTo is { } to && route.Args.ContainsKey("festivalId"))
            route = route with { Args = new Dictionary<string, string>(route.Args) { ["dateFrom"] = from.ToString("yyyy-MM-dd"), ["dateTo"] = to.ToString("yyyy-MM-dd") } };
        return new NoticeItemDto
        {
            Id = r.Id,
            Kind = r.Kind,
            TripId = r.TripId == NoticeKinds.NoTrip ? null : r.TripId,
            TripTitle = r.TripTitle,
            ActorUserId = r.ActorUserId,
            ActorName = r.ActorName,
            RefId = r.RefId,
            Amount = r.Amount,
            RefTitle = r.RefTitle,
            RefDateFrom = r.RefDateFrom,
            RefDateTo = r.RefDateTo,
            Travellers = r.Travellers,
            Count = r.Count,
            CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
            UpdatedAt = DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc),
            IsRead = r.ReadAt != null,
            Route = route.Route,
            Args = route.Args,
        };
    }

    private static NpgsqlParameter User(Guid userId) => new("user", NpgsqlDbType.Uuid) { Value = userId };

    // The owner kinds route by the viewer's listing kind; a Seeker or an unknown role routes as a stay (never throws).
    public static string? ViewerKind(Guid? roleId)
        => roleId == UserRoles.TaxiDriverId ? ListingKinds.Taxi : roleId == UserRoles.StayOwnerId ? ListingKinds.Stay : null;

    public sealed class Row
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = string.Empty;
        public Guid TripId { get; set; }
        public string? TripTitle { get; set; }
        public Guid? ActorUserId { get; set; }
        public string? ActorName { get; set; }
        public Guid? RefId { get; set; }
        public int? Amount { get; set; }
        public int Count { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? ReadAt { get; set; }
        public string? RefTitle { get; set; }
        public DateOnly? RefDateFrom { get; set; }
        public DateOnly? RefDateTo { get; set; }
        public Guid? ViewerRoleId { get; set; }
        public int? Travellers { get; set; }
    }
}
