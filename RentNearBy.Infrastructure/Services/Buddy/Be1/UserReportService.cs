using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Buddy.Be1;

public enum ReportFiled { Filed, InvalidReason }

// B15, B16, admin hold and the person-report counts. A report goes to the admin queue only (NotifyOwner = false).
public sealed class UserReportService(ApplicationDbContext db, IRabbitMqPublisher publisher, IMemoryCache cache, ILogger<UserReportService> logger)
{
    public const string ReasonsCacheKey = "report_reasons_user";
    private static readonly TimeSpan ReasonsTtl = TimeSpan.FromHours(24);

    private static readonly string ReportStateSql = $"""
        SELECT rr."Name" AS "ReasonName",
               me."Name" AS "ReporterName", me."PhoneNumber" AS "ReporterMobile",
               t."Id" AS "TargetId", t."Name" AS "TargetName", t."PhoneNumber" AS "TargetMobile", t."Username" AS "TargetUsername"
        FROM (SELECT 1) one
        LEFT JOIN "ReportReasons" rr ON rr."Id" = @reason AND rr."Scope" = '{ReportScopes.User}'
        LEFT JOIN "Users" me ON me."Id" = @me
        LEFT JOIN "Users" t ON t."Id" = @him
        """;

    private static readonly string InsertReportSql = $"""
        INSERT INTO "ListingReports" ("Id", "ListingId", "ListingType", "ReporterUserId", "ReporterName", "ReporterMobile",
                                      "ReportedUserId", "ReportedName", "ReportedMobile", "ReasonId", "Details", "Status", "CreatedAt",
                                      "ReportedUsername", "Context")
        VALUES (gen_random_uuid(), @him, '{ReportScopes.User}', @me, @reporterName, @reporterMobile,
                @him, @targetName, @targetMobile, @reason, @details, 'Pending', now(), @username, @context)
        ON CONFLICT ("ReporterUserId", "ListingId") WHERE "Status" = 'Pending' DO NOTHING
        """;

    // L9: 3 reports from 3 different people in 30 days hold new requests for 30 days (never shortens a longer hold).
    private static readonly string AutoHoldSql = $"""
        UPDATE "Users" SET "BuddyRequestsHeldUntil" = now() + {Be1Sql.Days(BuddyLimits.AutoHoldFor)}
        WHERE "Id" = @him AND ("BuddyRequestsHeldUntil" IS NULL OR "BuddyRequestsHeldUntil" < now())
          AND (SELECT count(DISTINCT r."ReporterUserId") FROM "ListingReports" r
               WHERE r."ReportedUserId" = @him AND r."ListingType" = '{ReportScopes.User}'
                 AND r."CreatedAt" > now() - {Be1Sql.Days(BuddyLimits.AutoHoldWindow)}) >= {BuddyLimits.AutoHoldReporters}
        """;

    private static readonly string CountsSql = $"""
        SELECT (SELECT count(*) FROM "ListingReports" r WHERE r."ReportedUserId" = @him AND r."ListingType" = '{ReportScopes.User}'
                  AND r."CreatedAt" > now() - interval '30 days')::int AS "Reports30d",
               (SELECT count(DISTINCT r."ReporterUserId") FROM "ListingReports" r WHERE r."ReportedUserId" = @him
                  AND r."ListingType" = '{ReportScopes.User}' AND r."CreatedAt" > now() - interval '30 days')::int AS "Reporters30d",
               (SELECT count(*) FROM "UserBlocks" b WHERE b."BlockedId" = @him AND b."CreatedAt" > now() - interval '30 days')::int AS "BlocksReceived30d",
               (SELECT count(*) FROM "BuddyRequests" q WHERE q."FromUserId" = @him AND q."CreatedAt" > now() - interval '7 days')::int AS "RequestsSent7d",
               (SELECT count(*) FROM "BuddyRequests" q WHERE q."FromUserId" = @him AND q."CreatedAt" > now() - interval '7 days'
                  AND q."Status" = '{BuddyRequestStatuses.Accepted}')::int AS "RequestsAccepted7d"
        """;

    public async Task<ReportFiled> ReportAsync(Guid me, Guid him, Guid reasonId, string? note, string? context, CancellationToken ct = default)
    {
        var s = (await db.Database.SqlQueryRaw<ReportState>(ReportStateSql,
            Be1Sql.Uuid("reason", reasonId), Be1Sql.Uuid("me", me), Be1Sql.Uuid("him", him)).ToListAsync(ct))[0];
        if (s.ReasonName is null) return ReportFiled.InvalidReason;
        if (s.TargetId is null) return ReportFiled.Filed;

        var inserted = await db.Database.ExecuteSqlRawAsync(InsertReportSql,
        [
            Be1Sql.Uuid("him", him), Be1Sql.Uuid("me", me), Be1Sql.Text("reporterName", s.ReporterName ?? "Unknown"),
            Be1Sql.Text("reporterMobile", s.ReporterMobile ?? string.Empty), Be1Sql.Text("targetName", s.TargetName ?? "Unknown"),
            Be1Sql.Text("targetMobile", s.TargetMobile ?? string.Empty), Be1Sql.Uuid("reason", reasonId),
            Be1Sql.Text("details", BuddyRules.ReportDetails(reasonId, note)),
            NullableText("username", s.TargetUsername), NullableText("context", context),
        ], ct);
        if (inserted == 0) return ReportFiled.Filed;

        await db.Database.ExecuteSqlRawAsync(AutoHoldSql, [Be1Sql.Uuid("him", him)], ct);

        var message = new ReportFiledMessage
        {
            OwnerId = him,
            ListingId = him,
            ListingType = ReportScopes.User,
            ReasonName = s.ReasonName,
            ListingTitle = $"{s.TargetName} ({s.TargetUsername})",
            NotifyOwner = false,
        };
        try
        {
            await publisher.PublishAsync("report.filed", JsonSerializer.Serialize(message));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "buddy.report.publish_failed");
        }
        return ReportFiled.Filed;
    }

    public async Task<IReadOnlyList<UserReportReasonDto>> ReasonsAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(ReasonsCacheKey, out IReadOnlyList<UserReportReasonDto>? cached) && cached is not null) return cached;
        var reasons = await db.ReportReasons.AsNoTracking()
            .Where(r => r.Scope == ReportScopes.User)
            .OrderBy(r => r.SortOrder)
            .Select(r => new UserReportReasonDto { Id = r.Id, Name = r.Name, Description = r.Description })
            .ToListAsync(ct);
        cache.Set(ReasonsCacheKey, (IReadOnlyList<UserReportReasonDto>)reasons, ReasonsTtl);
        return reasons;
    }

    // null = unknown user.
    public async Task<BuddyHoldDto?> SetHoldAsync(Guid userId, DateTime? until, CancellationToken ct = default)
    {
        var value = until is null ? (DateTime?)null : DateTime.SpecifyKind(until.Value, DateTimeKind.Utc);
        var n = await db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.BuddyRequestsHeldUntil, value), ct);
        return n == 0 ? null : new BuddyHoldDto { UserId = userId, BuddyRequestsHeldUntil = value };
    }

    public async Task<PersonReportCountsDto> CountsAsync(Guid userId, CancellationToken ct = default)
        => (await db.Database.SqlQueryRaw<PersonReportCountsDto>(CountsSql, Be1Sql.Uuid("him", userId)).ToListAsync(ct))[0];

    private static Npgsql.NpgsqlParameter NullableText(string name, string? value)
        => new(name, NpgsqlTypes.NpgsqlDbType.Varchar) { Value = (object?)value ?? DBNull.Value };

    private sealed class ReportState
    {
        public string? ReasonName { get; set; }
        public string? ReporterName { get; set; }
        public string? ReporterMobile { get; set; }
        public Guid? TargetId { get; set; }
        public string? TargetName { get; set; }
        public string? TargetMobile { get; set; }
        public string? TargetUsername { get; set; }
    }
}
