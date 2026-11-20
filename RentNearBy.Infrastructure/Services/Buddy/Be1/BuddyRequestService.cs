using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Buddy.Be1;

internal static class Be1Sql
{
    public static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };

    public static NpgsqlParameter Uuids(string name, IReadOnlyCollection<Guid> values)
        => new(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = values.ToArray() };

    public static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Text) { Value = value };

    public static NpgsqlParameter Int(string name, int value) => new(name, NpgsqlDbType.Integer) { Value = value };

    public static NpgsqlParameter At(string name, DateTime value) => new(name, NpgsqlDbType.TimestampTz) { Value = DateTime.SpecifyKind(value, DateTimeKind.Utc) };

    public static string Days(TimeSpan span) => $"interval '{span.TotalDays.ToString(CultureInfo.InvariantCulture)} days'";

    // Open, or silent (declined, or closed by a block of the sender): counts as waiting before ExpiresAt.
    public static string WaitingFilter(string alias = "") =>
        $"""({alias}"Status" = '{BuddyRequestStatuses.Pending}' OR {alias}"Status" = '{BuddyRequestStatuses.Declined}' OR ({alias}"Status" = '{BuddyRequestStatuses.ClosedByBlock}' AND {alias}"BlockedSender"))""";

    // The receiver's BuddyRequest notice goes when he has no open request left (contract 4.1).
    public static readonly string DeleteRequestNoticesSql = $"""
        DELETE FROM "UserNotices" n
        WHERE n."Kind" = '{NoticeKinds.BuddyRequest}' AND n."TripId" = '{NoticeKinds.NoTrip}' AND n."UserId" = ANY(@users)
          AND NOT EXISTS (SELECT 1 FROM "BuddyRequests" r
                          WHERE r."ToUserId" = n."UserId" AND r."Status" = '{BuddyRequestStatuses.Pending}' AND r."ExpiresAt" > now())
        """;

    public static Task DeleteRequestNoticesAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> users, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(DeleteRequestNoticesSql, [Uuids("users", users)], ct);

    public static readonly string AcceptSql = $"""
        UPDATE "BuddyRequests" SET "Status" = '{BuddyRequestStatuses.Accepted}', "RespondedAt" = now(), "EndedAt" = now()
        WHERE "Id" = @id AND "Status" = '{BuddyRequestStatuses.Pending}' AND "ExpiresAt" > now()
        """;

    public const string InsertBuddiesSql = """
        INSERT INTO "Buddies" ("UserId", "BuddyUserId", "CreatedAt", "RequestId", "SeenAt")
        VALUES (@a, @b, now(), @id, NULL), (@b, @a, now(), @id, NULL)
        ON CONFLICT DO NOTHING
        """;

    public static PersonDto Person(Guid id, string? name, string? username)
        => new() { UserId = id, Name = name ?? string.Empty, Username = username, Initial = MoneyPeople.Initial(name) };
}

public enum SendKind { Created, Existing, Accepted }

public sealed record SendOutcome(SendKind Kind, BuddyRequestCreatedDto? Request, BuddyRequestAcceptedDto? Accepted);

// B2, B5, B6, B7. Lock order: sender -> pair -> rows (contract 5.1). Every unit retried once on 40P01/40001.
public sealed class BuddyRequestService(ApplicationDbContext db, INoticeWriter notices, IBuddyLiveHints hints, ILogger<BuddyRequestService> logger)
{
    private static readonly string SendStateSql = $"""
        SELECT COALESCE(me."Name", '') AS "MyName", me."Username" AS "MyUsername",
               COALESCE(me."BuddyRequestsHeldUntil" > now(), FALSE) AS "OnHold",
               t."Id" AS "TargetId", t."Name" AS "TargetName", t."Username" AS "TargetUsername",
               EXISTS (SELECT 1 FROM "UserBlocks" ub
                       WHERE (ub."BlockerId" = @me AND ub."BlockedId" = @to) OR (ub."BlockerId" = @to AND ub."BlockedId" = @me)) AS "Blocked",
               EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @me AND b."BuddyUserId" = @to) AS "IsBuddy",
               (SELECT count(*) FROM "Buddies" b WHERE b."UserId" = @me)::int AS "MyBuddies",
               (SELECT count(*) FROM "Buddies" b WHERE b."UserId" = @to)::int AS "TheirBuddies",
               (SELECT count(*) FROM "BuddyRequests" w WHERE w."FromUserId" = @me AND w."ExpiresAt" > now() AND {Be1Sql.WaitingFilter("w.")})::int AS "Waiting",
               c."Id" AS "ClientId", c."ExpiresAt" AS "ClientExpiresAt",
               r."Id" AS "ReqId", r."Status" AS "ReqStatus", r."CreatedAt" AS "ReqCreatedAt", r."ExpiresAt" AS "ReqExpiresAt", r."BlockedSender" AS "ReqBlockedSender",
               p."Id" AS "TheirPendingId", now() AS "Now"
        FROM "Users" me
        LEFT JOIN "Users" t ON t."Id" = @to AND t."IsActive"
        LEFT JOIN "BuddyRequests" c ON c."FromUserId" = @me AND c."ClientRequestId" = @client
        LEFT JOIN LATERAL (
          SELECT x."Id", x."Status", x."CreatedAt", x."ExpiresAt", x."BlockedSender" FROM "BuddyRequests" x
          WHERE x."PairLowId" = LEAST(@me, @to) AND x."PairHighId" = GREATEST(@me, @to) AND x."FromUserId" = @me
          ORDER BY x."CreatedAt" DESC LIMIT 1) r ON TRUE
        LEFT JOIN LATERAL (
          SELECT y."Id" FROM "BuddyRequests" y
          WHERE y."PairLowId" = LEAST(@me, @to) AND y."PairHighId" = GREATEST(@me, @to) AND y."FromUserId" = @to
            AND y."Status" = '{BuddyRequestStatuses.Pending}' AND y."ExpiresAt" > now()
          LIMIT 1) p ON TRUE
        WHERE me."Id" = @me
        """;

    private static readonly string ExpirePairSql = $"""
        UPDATE "BuddyRequests" SET "Status" = '{BuddyRequestStatuses.Expired}', "EndedAt" = now()
        WHERE "PairLowId" = LEAST(@me, @to) AND "PairHighId" = GREATEST(@me, @to)
          AND "Status" = '{BuddyRequestStatuses.Pending}' AND "ExpiresAt" <= now()
        """;

    private static readonly string InsertSql = $"""
        INSERT INTO "BuddyRequests" ("Id", "FromUserId", "ToUserId", "PairLowId", "PairHighId", "Status", "Source",
                                     "ClientRequestId", "CreatedAt", "ExpiresAt", "BlockedSender")
        VALUES (gen_random_uuid(), @me, @to, LEAST(@me, @to), GREATEST(@me, @to), '{BuddyRequestStatuses.Pending}', @source,
                @client, now(), now() + {Be1Sql.Days(BuddyLimits.RequestExpiry)}, FALSE)
        ON CONFLICT DO NOTHING
        RETURNING "Id", "ExpiresAt"
        """;

    private const string RequestOfSql = """
        SELECT "Id", "FromUserId", "ToUserId", "Status" FROM "BuddyRequests" WHERE "Id" = @id AND (@asSender AND "FromUserId" = @me OR NOT @asSender AND "ToUserId" = @me)
        """;

    private const string AcceptStateSql = """
        SELECT r."Status", r."ExpiresAt" > now() AS "Live",
               EXISTS (SELECT 1 FROM "Buddies" b WHERE b."UserId" = @me AND b."BuddyUserId" = r."FromUserId") AS "IsBuddy",
               (SELECT count(*) FROM "Buddies" b WHERE b."UserId" = @me)::int AS "MyBuddies",
               (SELECT count(*) FROM "Buddies" b WHERE b."UserId" = r."FromUserId")::int AS "TheirBuddies",
               COALESCE(me."Name", '') AS "MyName", s."Name" AS "SenderName", s."Username" AS "SenderUsername"
        FROM "BuddyRequests" r
        JOIN "Users" me ON me."Id" = @me
        LEFT JOIN "Users" s ON s."Id" = r."FromUserId"
        WHERE r."Id" = @id
        """;

    private static readonly string DeclineSql = $"""
        UPDATE "BuddyRequests" SET "Status" = '{BuddyRequestStatuses.Declined}', "RespondedAt" = now(), "EndedAt" = now()
        WHERE "Id" = @id AND "Status" = '{BuddyRequestStatuses.Pending}'
        """;

    private static readonly string CancelSql = $"""
        UPDATE "BuddyRequests" SET "Status" = '{BuddyRequestStatuses.Cancelled}', "EndedAt" = COALESCE("EndedAt", now())
        WHERE "Id" = @id AND {Be1Sql.WaitingFilter()}
        """;

    private const string StatusSql = """SELECT "Status" AS "Value" FROM "BuddyRequests" WHERE "Id" = @id""";

    public async Task<BuddyResult<SendOutcome>> SendAsync(Guid callerId, Guid toUserId, string source, Guid clientRequestId, CancellationToken ct = default)
    {
        if (callerId == toUserId) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.SelfRequest);
        var pushed = false;
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            pushed = false;
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockSenderAsync(db, callerId, c);
            await BuddyLocks.LockPairAsync(db, callerId, toUserId, c);
            var pair = new object[] { Be1Sql.Uuid("me", callerId), Be1Sql.Uuid("to", toUserId) };
            await db.Database.ExecuteSqlRawAsync(ExpirePairSql, pair, c);
            var s = (await db.Database.SqlQueryRaw<SendState>(SendStateSql,
                Be1Sql.Uuid("me", callerId), Be1Sql.Uuid("to", toUserId), Be1Sql.Uuid("client", clientRequestId)).ToListAsync(c))[0];

            if (s.ClientId is { } replayId)
                return BuddyResult<SendOutcome>.Success(Existing(replayId, s.ClientExpiresAt!.Value));
            if (s.TargetId is null || s.Blocked) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.PersonNotFound);
            if (s.OnHold) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.BuddyRequestsOnHold);
            if (s.IsBuddy) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.AlreadyBuddies);

            var mine = s.ReqId is { } reqId
                ? new RequestFacts(reqId, callerId, s.ReqStatus!, s.ReqCreatedAt!.Value, s.ReqExpiresAt!.Value, s.ReqBlockedSender ?? false)
                : null;
            if (mine is not null && BuddyRules.IsWaitingForSender(mine, s.Now))
                return BuddyResult<SendOutcome>.Success(Existing(mine.Id, mine.ExpiresAt));

            if (s.TheirPendingId is { } crossingId)
            {
                if (s.MyBuddies >= BuddyLimits.MaxBuddies) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.BuddyLimit);
                if (s.TheirBuddies >= BuddyLimits.MaxBuddies) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.CannotAddNow);
                if (!await AcceptCoreAsync(crossingId, accepterId: callerId, senderId: toUserId, s.MyName, c))
                    return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.CannotAddNow);
                await tx.CommitAsync(c);
                pushed = true;
                var buddy = Be1Sql.Person(toUserId, s.TargetName, s.TargetUsername);
                return BuddyResult<SendOutcome>.Success(new SendOutcome(SendKind.Accepted, null, new BuddyRequestAcceptedDto { Buddy = buddy }));
            }

            if (BuddyRules.InCooldown(mine, s.Now)) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.TryLater);
            if (s.Waiting >= BuddyLimits.MaxWaitingRequests) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.WaitingLimit);
            if (s.MyBuddies >= BuddyLimits.MaxBuddies) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.BuddyLimit);
            if (s.TheirBuddies >= BuddyLimits.MaxBuddies) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.CannotAddNow);

            var inserted = await db.Database.SqlQueryRaw<InsertedRow>(InsertSql,
                Be1Sql.Uuid("me", callerId), Be1Sql.Uuid("to", toUserId), Be1Sql.Text("source", source),
                Be1Sql.Uuid("client", clientRequestId)).ToListAsync(c);
            if (inserted.Count == 0) return BuddyResult<SendOutcome>.Fail(BuddyErrorTypes.CannotAddNow);
            var row = inserted[0];

            await notices.UpsertAsync([new NoticeUpsert(toUserId, NoticeKinds.BuddyRequest, NoticeKinds.NoTrip, callerId)], c);
            await notices.PushAsync([BuddyPushCopy.BuddyRequestReceived(toUserId, row.Id, s.MyName, s.MyUsername ?? string.Empty)], c);
            await tx.CommitAsync(c);
            pushed = true;
            return BuddyResult<SendOutcome>.Success(new SendOutcome(SendKind.Created,
                new BuddyRequestCreatedDto { RequestId = row.Id, ExpiresAt = row.ExpiresAt }, null));
        }, ct, logger);

        if (pushed)
        {
            notices.Wake();
            await hints.BuddiesChangedAsync([callerId, toUserId]);
        }
        return result;
    }

    public async Task<BuddyResult<BuddyAcceptDto>> AcceptAsync(Guid callerId, Guid requestId, CancellationToken ct = default)
    {
        var target = await ReadOwnAsync(requestId, callerId, asSender: false, ct);
        if (target is null) return BuddyResult<BuddyAcceptDto>.Fail(BuddyErrorTypes.RequestEnded);
        var senderId = target.FromUserId;
        var pushed = false;
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            pushed = false;
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, callerId, senderId, c);
            var s = (await db.Database.SqlQueryRaw<AcceptState>(AcceptStateSql,
                Be1Sql.Uuid("me", callerId), Be1Sql.Uuid("id", requestId)).ToListAsync(c)).FirstOrDefault();
            if (s is null) return BuddyResult<BuddyAcceptDto>.Fail(BuddyErrorTypes.RequestEnded);
            var buddy = new BuddyAcceptDto { Buddy = Be1Sql.Person(senderId, s.SenderName, s.SenderUsername) };
            if (s.Status == BuddyRequestStatuses.Accepted && s.IsBuddy) return BuddyResult<BuddyAcceptDto>.Success(buddy);
            if (s.Status != BuddyRequestStatuses.Pending || !s.Live || s.SenderName is null)
                return BuddyResult<BuddyAcceptDto>.Fail(BuddyErrorTypes.RequestEnded);
            if (s.MyBuddies >= BuddyLimits.MaxBuddies) return BuddyResult<BuddyAcceptDto>.Fail(BuddyErrorTypes.BuddyLimit);
            if (s.TheirBuddies >= BuddyLimits.MaxBuddies) return BuddyResult<BuddyAcceptDto>.Fail(BuddyErrorTypes.CannotAddNow);
            if (!await AcceptCoreAsync(requestId, callerId, senderId, s.MyName, c))
                return BuddyResult<BuddyAcceptDto>.Fail(BuddyErrorTypes.RequestEnded);
            await tx.CommitAsync(c);
            pushed = true;
            return BuddyResult<BuddyAcceptDto>.Success(buddy);
        }, ct, logger);

        if (pushed)
        {
            notices.Wake();
            await hints.BuddiesChangedAsync([callerId, senderId]);
        }
        return result;
    }

    // true = done (also an ended request that was never addressed to the caller answers false).
    public async Task<bool> DeclineAsync(Guid callerId, Guid requestId, CancellationToken ct = default)
    {
        var target = await ReadOwnAsync(requestId, callerId, asSender: false, ct);
        if (target is null) return false;
        if (target.Status != BuddyRequestStatuses.Pending) return true;
        await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, callerId, target.FromUserId, c);
            if (await db.Database.ExecuteSqlRawAsync(DeclineSql, [Be1Sql.Uuid("id", requestId)], c) > 0)
                await Be1Sql.DeleteRequestNoticesAsync(db, [callerId], c);
            await tx.CommitAsync(c);
        }, ct, logger);
        return true;
    }

    public async Task<BuddyResult<bool>> CancelAsync(Guid callerId, Guid requestId, CancellationToken ct = default)
    {
        var target = await ReadOwnAsync(requestId, callerId, asSender: true, ct);
        if (target is null) return BuddyResult<bool>.Fail(BuddyErrorTypes.RequestEnded);
        var withdrawn = false;
        var result = await PgRetry.OnceAsync(db, async c =>
        {
            withdrawn = false;
            await using var tx = await db.Database.BeginTransactionAsync(c);
            await BuddyLocks.LockPairAsync(db, callerId, target.ToUserId, c);
            var status = (await db.Database.SqlQueryRaw<string>(StatusSql, Be1Sql.Uuid("id", requestId)).ToListAsync(c)).FirstOrDefault();
            if (status == BuddyRequestStatuses.Accepted) return BuddyResult<bool>.Fail(BuddyErrorTypes.AlreadyBuddies);
            var open = await db.Database.ExecuteSqlRawAsync(CancelSql, [Be1Sql.Uuid("id", requestId)], c) > 0 && status == BuddyRequestStatuses.Pending;
            if (open) await Be1Sql.DeleteRequestNoticesAsync(db, [target.ToUserId], c);
            await tx.CommitAsync(c);
            withdrawn = open;
            return BuddyResult<bool>.Success(true);
        }, ct, logger);
        if (withdrawn) await hints.BuddiesChangedAsync([target.ToUserId]);
        return result;
    }

    private async Task<bool> AcceptCoreAsync(Guid requestId, Guid accepterId, Guid senderId, string accepterName, CancellationToken ct)
    {
        if (await db.Database.ExecuteSqlRawAsync(Be1Sql.AcceptSql, [Be1Sql.Uuid("id", requestId)], ct) == 0) return false;
        await db.Database.ExecuteSqlRawAsync(Be1Sql.InsertBuddiesSql,
            [Be1Sql.Uuid("a", accepterId), Be1Sql.Uuid("b", senderId), Be1Sql.Uuid("id", requestId)], ct);
        await Be1Sql.DeleteRequestNoticesAsync(db, [accepterId], ct);
        await notices.PushAsync([BuddyPushCopy.BuddyRequestAccepted(senderId, requestId, accepterName)], ct);
        return true;
    }

    private async Task<OwnRequest?> ReadOwnAsync(Guid requestId, Guid callerId, bool asSender, CancellationToken ct)
        => (await db.Database.SqlQueryRaw<OwnRequest>(RequestOfSql, Be1Sql.Uuid("id", requestId), Be1Sql.Uuid("me", callerId),
            new NpgsqlParameter("asSender", NpgsqlDbType.Boolean) { Value = asSender }).ToListAsync(ct)).FirstOrDefault();

    private static SendOutcome Existing(Guid id, DateTime expiresAt)
        => new(SendKind.Existing, new BuddyRequestCreatedDto { RequestId = id, ExpiresAt = expiresAt }, null);

    private sealed class SendState
    {
        public string MyName { get; set; } = string.Empty;
        public string? MyUsername { get; set; }
        public bool OnHold { get; set; }
        public Guid? TargetId { get; set; }
        public string? TargetName { get; set; }
        public string? TargetUsername { get; set; }
        public bool Blocked { get; set; }
        public bool IsBuddy { get; set; }
        public int MyBuddies { get; set; }
        public int TheirBuddies { get; set; }
        public int Waiting { get; set; }
        public Guid? ClientId { get; set; }
        public DateTime? ClientExpiresAt { get; set; }
        public Guid? ReqId { get; set; }
        public string? ReqStatus { get; set; }
        public DateTime? ReqCreatedAt { get; set; }
        public DateTime? ReqExpiresAt { get; set; }
        public bool? ReqBlockedSender { get; set; }
        public Guid? TheirPendingId { get; set; }
        public DateTime Now { get; set; }
    }

    private sealed class InsertedRow
    {
        public Guid Id { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    private sealed class OwnRequest
    {
        public Guid Id { get; set; }
        public Guid FromUserId { get; set; }
        public Guid ToUserId { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    private sealed class AcceptState
    {
        public string Status { get; set; } = string.Empty;
        public bool Live { get; set; }
        public bool IsBuddy { get; set; }
        public int MyBuddies { get; set; }
        public int TheirBuddies { get; set; }
        public string MyName { get; set; } = string.Empty;
        public string? SenderName { get; set; }
        public string? SenderUsername { get; set; }
    }
}
