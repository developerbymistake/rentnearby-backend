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
    private const string PageColumns = """
        SELECT n."Id", n."Kind", n."TripId", t."Title" AS "TripTitle", n."ActorUserId", a."Name" AS "ActorName", n."RefId", n."Amount",
               n."Count", n."CreatedAt", n."UpdatedAt", n."ReadAt"
        FROM "UserNotices" n
        LEFT JOIN "Trips" t ON t."Id" = n."TripId"
        LEFT JOIN "Users" a ON a."Id" = n."ActorUserId"
        WHERE n."UserId" = @user
        """;

    internal const string FirstPageSql = PageColumns + """

        ORDER BY n."UpdatedAt" DESC, n."Id" DESC
        LIMIT @take
        """;

    internal const string NextPageSql = PageColumns + """

          AND (n."UpdatedAt", n."Id") < (@at, @id)
        ORDER BY n."UpdatedAt" DESC, n."Id" DESC
        LIMIT @take
        """;

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
        var route = NoticeRoutes.For(r.Kind, r.TripId, r.RefId);
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
            Count = r.Count,
            CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
            UpdatedAt = DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc),
            IsRead = r.ReadAt != null,
            Route = route.Route,
            Args = route.Args,
        };
    }

    private static NpgsqlParameter User(Guid userId) => new("user", NpgsqlDbType.Uuid) { Value = userId };

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
    }
}
