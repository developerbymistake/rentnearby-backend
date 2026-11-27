using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Owner.Quote;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Owner.Quote;

// POST /stays/me/quotation/reserve (ARCHITECTURE 4): exactly two DB commands per reserve.
// 1. One indexed pre-read (stay, terms, assets, entitled plan row) without a lock.
// 2. One NpgsqlBatch of three statements with no explicit transaction: PostgreSQL runs it as ONE implicit transaction (one Sync),
//    the counter row lock taken by S2 is released when S3 ends on the server and is never held across a network round trip.
//    S3 starts its READ COMMITTED snapshot after the lock is held, so two taps / two phones are strictly serial: the first counts,
//    the second sees the first one's issue row (Free) or takes the next number. A single statement could not: its snapshot would be
//    taken before it waited on the lock and the second tap would count twice.
public sealed class StayQuoteReserveService(
    ApplicationDbContext db, IPlanEntitlementService entitlements, TimeProvider clock, ILogger<StayQuoteReserveService> logger)
{
    public const string UnauthorizedType = StayQuoteProfileService.UnauthorizedType;

    internal sealed class PreRow
    {
        public string? Name { get; set; }
        public string Status { get; set; } = string.Empty;
        public short? TermsVersion { get; set; }
        public int? AssetsVersion { get; set; }
        public Guid? PlanRowId { get; set; }
        public Guid? PlanId { get; set; }
        public string? PlanSource { get; set; }
        public DateTime? PlanStartsAt { get; set; }
        public DateTime? PlanEndsAt { get; set; }
    }

    internal static readonly string PreReadSql = $"""
        SELECT s."Name" AS "Name", s."Status" AS "Status",
               (SELECT max(a."Version") FROM "StayQuoteTermsAcceptances" a WHERE a."UserId" = @user) AS "TermsVersion",
               (SELECT p."AssetsVersion" FROM "StayQuoteProfiles" p WHERE p."UserId" = @user) AS "AssetsVersion",
               up."Id" AS "PlanRowId", up."PlanId" AS "PlanId", up."Source" AS "PlanSource", up."StartsAt" AS "PlanStartsAt", up."EndsAt" AS "PlanEndsAt"
        FROM "StayListings" s
        LEFT JOIN LATERAL (
            SELECT u."Id", u."PlanId", u."Source", u."StartsAt", u."EndsAt" FROM "UserPlans" u
            WHERE u."UserId" = @user AND u."FeatureKey" = '{ListingKinds.Stay}' AND {PlanRowSql.EntitledSql("u")}
            ORDER BY u."EndsAt" DESC LIMIT 1) up ON true
        WHERE s."UserId" = @user AND s."IsDeleted" = false
        """;

    // S1: make sure the lock row exists. $1 user, $2 month, $3 now.
    internal const string EnsureCounterSql = """
        INSERT INTO "StayQuoteCounters" ("UserId", "MonthKey", "Used", "NextQuoteNo", "NextProformaNo", "UpdatedAt")
        VALUES ($1, $2, 0, 1, 1, $3) ON CONFLICT ("UserId") DO NOTHING
        """;

    // S2: serialise everything of this owner.
    internal const string LockCounterSql = """
        SELECT 1 FROM "StayQuoteCounters" WHERE "UserId" = $1 FOR UPDATE
        """;

    // S3: decide and write. $1 user, $2 document, $3 kind, $4 month, $5 now, $6 window start (now - 24 h), $7 limit, $8 max free uses.
    // The month key never moves backwards: a request that read the clock at 23:59:59 IST and then waited on the lock behind a
    // request of the new month counts in the stored (newer) month instead of resetting it.
    internal const string DecideSql = """
        WITH prior AS (
            SELECT "Number", "IssuedAt", "WindowAt", "FreeUses" FROM "StayQuoteIssues"
            WHERE "UserId" = $1 AND "DocumentId" = $2 AND "Kind" = $3
        ), free AS (
            SELECT * FROM prior WHERE "WindowAt" > $6 AND "FreeUses" < $8
        ), freeuse AS (
            UPDATE "StayQuoteIssues" i SET "FreeUses" = i."FreeUses" + 1
            WHERE i."UserId" = $1 AND i."DocumentId" = $2 AND i."Kind" = $3 AND EXISTS (SELECT 1 FROM free)
            RETURNING i."Number", i."IssuedAt", i."WindowAt", i."FreeUses"
        ), bump AS (
            UPDATE "StayQuoteCounters" c SET
                "MonthKey" = GREATEST(c."MonthKey", $4),
                "Used" = (CASE WHEN c."MonthKey" >= $4 THEN c."Used" ELSE 0 END) + 1,
                "NextQuoteNo" = c."NextQuoteNo" + CASE WHEN $3 = 0 AND NOT EXISTS (SELECT 1 FROM prior) THEN 1 ELSE 0 END,
                "NextProformaNo" = c."NextProformaNo" + CASE WHEN $3 = 1 AND NOT EXISTS (SELECT 1 FROM prior) THEN 1 ELSE 0 END,
                "UpdatedAt" = $5
            WHERE c."UserId" = $1
              AND NOT EXISTS (SELECT 1 FROM free)
              AND (CASE WHEN c."MonthKey" >= $4 THEN c."Used" ELSE 0 END) < $7
            RETURNING c."Used", c."NextQuoteNo", c."NextProformaNo"
        ), ins AS (
            INSERT INTO "StayQuoteIssues" ("UserId", "DocumentId", "Kind", "Number", "IssuedAt", "WindowAt", "Downloads", "FreeUses")
            SELECT $1, $2, $3, (CASE WHEN $3 = 0 THEN b."NextQuoteNo" ELSE b."NextProformaNo" END) - 1, $5, $5, 1, 0
            FROM bump b WHERE NOT EXISTS (SELECT 1 FROM prior)
            RETURNING "Number", "IssuedAt", "WindowAt", "FreeUses"
        ), again AS (
            UPDATE "StayQuoteIssues" i SET "WindowAt" = $5, "Downloads" = i."Downloads" + 1, "FreeUses" = 0
            WHERE i."UserId" = $1 AND i."DocumentId" = $2 AND i."Kind" = $3
              AND EXISTS (SELECT 1 FROM prior) AND NOT EXISTS (SELECT 1 FROM free) AND EXISTS (SELECT 1 FROM bump)
            RETURNING i."Number", i."IssuedAt", i."WindowAt", i."FreeUses"
        ), hit AS (
            SELECT * FROM freeuse UNION ALL SELECT * FROM ins UNION ALL SELECT * FROM again
        )
        SELECT
            CASE WHEN EXISTS (SELECT 1 FROM free) THEN 'Free'
                 WHEN EXISTS (SELECT 1 FROM bump) THEN 'Counted' ELSE 'Limit' END AS "Outcome",
            (SELECT "Number" FROM hit) AS "Number",
            (SELECT "IssuedAt" FROM hit) AS "IssuedAt",
            (SELECT "WindowAt" FROM hit) AS "WindowAt",
            (SELECT "FreeUses" FROM hit) AS "FreeUses",
            COALESCE((SELECT "Used" FROM bump),
                     (SELECT CASE WHEN "MonthKey" >= $4 THEN "Used" ELSE 0 END FROM "StayQuoteCounters" WHERE "UserId" = $1)) AS "Used"
        """;

    public const string OutcomeFree = "Free";
    public const string OutcomeCounted = "Counted";
    public const string OutcomeLimit = "Limit";

    internal sealed record Decision(string Outcome, int? Number, DateTime? IssuedAt, DateTime? WindowAt, int? FreeUses, int Used);

    public async Task<QuoteResult<QuoteReserveDto>> ReserveAsync(Guid userId, Guid documentId, short kind, CancellationToken ct)
    {
        var pre = (await db.Database.SqlQueryRaw<PreRow>(PreReadSql, new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId })
            .ToListAsync(ct)).FirstOrDefault();
        if (pre == null) return QuoteResult<QuoteReserveDto>.Fail(QuoteErrors.StayNotFound);
        if (pre.Status != StayStatuses.Approved) return QuoteResult<QuoteReserveDto>.Fail(QuoteErrors.StayNotApproved);
        if ((pre.TermsVersion ?? 0) < QuoteTerms.CurrentVersion)
            return QuoteResult<QuoteReserveDto>.Fail(QuoteErrors.TermsNotAccepted, new { currentVersion = (int)QuoteTerms.CurrentVersion });

        var limits = await entitlements.ComputeFromRowAsync(ListingKinds.Stay,
            StayQuoteProfileService.PlanRow(pre.PlanRowId, pre.PlanId, pre.PlanSource, pre.PlanStartsAt, pre.PlanEndsAt));
        var limit = limits.StayQuotationsPerMonth;
        var now = clock.GetUtcNow().UtcDateTime;
        var month = SocialMonth.Key(IstTime.TodayIst(now));
        var resetsAt = SocialMonth.ResetsAtUtc(now);

        Decision decision;
        try
        {
            decision = await PgRetry.OnceAsync(c => DecideAsync(userId, documentId, kind, month, now, limit, c), ct, logger);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // P9: the account was deleted at the same moment.
            return QuoteResult<QuoteReserveDto>.Fail(UnauthorizedType);
        }

        var usage = new QuoteUsageDto(decision.Used, limit, resetsAt, limits.CanUpgrade);
        if (decision.Outcome == OutcomeLimit)
            return QuoteResult<QuoteReserveDto>.Fail(QuoteErrors.MonthLimit,
                new QuoteLimitDataDto(PlanFeatureKeys.StayQuotationsPerMonth, limit, decision.Used, limits.CanUpgrade, resetsAt),
                QuoteLimitMessages.For(limit, limits.CanUpgrade));

        var number = decision.Number!.Value;
        var windowAt = DateTime.SpecifyKind(decision.WindowAt!.Value, DateTimeKind.Utc);
        return QuoteResult<QuoteReserveDto>.Success(new QuoteReserveDto(
            number,
            QuoteKinds.Display(kind, number),
            QuoteKinds.NameOf(kind),
            decision.Outcome == OutcomeCounted,
            windowAt + QuoteRules.FreeWindow,
            Math.Max(0, QuoteRules.MaxFreePerWindow - (decision.FreeUses ?? 0)),
            DateTime.SpecifyKind(decision.IssuedAt!.Value, DateTimeKind.Utc),
            usage,
            pre.Name,
            pre.AssetsVersion ?? 0));
    }

    internal static NpgsqlBatch BuildBatch(DbConnection connection, Guid userId, Guid documentId, short kind, int month, DateTime now, int limit)
    {
        var batch = new NpgsqlBatch((NpgsqlConnection)connection) { Timeout = QuoteRules.ReserveBatchTimeoutSeconds };
        batch.BatchCommands.Add(Command(EnsureCounterSql, Uuid(userId), Int(month), Time(now)));
        batch.BatchCommands.Add(Command(LockCounterSql, Uuid(userId)));
        batch.BatchCommands.Add(Command(DecideSql,
            Uuid(userId), Uuid(documentId), new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = kind }, Int(month), Time(now),
            Time(now - QuoteRules.FreeWindow), Int(limit), new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)QuoteRules.MaxFreePerWindow }));
        return batch;
    }

    private async Task<Decision> DecideAsync(Guid userId, Guid documentId, short kind, int month, DateTime now, int limit, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var batch = BuildBatch(connection, userId, documentId, kind, month, now, limit);
            await using var reader = await batch.ExecuteReaderAsync(ct);
            // S1 and S2 return no rows the caller needs; the decision is the only row of the last result set.
            do
            {
                if (reader.FieldCount == 6 && await reader.ReadAsync(ct))
                {
                    return new Decision(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                        reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                        reader.IsDBNull(4) ? null : reader.GetInt16(4),
                        reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5)));
                }
            } while (await reader.NextResultAsync(ct));
            throw new InvalidOperationException("Quote reserve batch returned no decision");
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }

    private static NpgsqlBatchCommand Command(string sql, params NpgsqlParameter[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddRange(parameters);
        return command;
    }

    private static NpgsqlParameter Uuid(Guid value) => new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = value };

    private static NpgsqlParameter Int(int value) => new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = value };

    private static NpgsqlParameter Time(DateTime value) => new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = DateTime.SpecifyKind(value, DateTimeKind.Utc) };
}

// The limit sheet line of the 409 (the app builds its own sheet from data; old builds toast this). Numbers come from the limits.
public static class QuoteLimitMessages
{
    public static string For(int limit, bool canUpgrade) => canUpgrade
        ? $"You have used {Count(limit)} on the Free plan this month."
        : $"You have used all {Count(limit)} this month.";

    private static string Count(int n) => n == 1 ? "1 quotation" : $"{n} quotations";
}
