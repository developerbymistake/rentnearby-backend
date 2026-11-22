using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner;

public sealed record OwnerContext(
    Guid UserId,
    string Kind,
    Guid ListingId,
    string Status,
    Guid? DistrictId,
    string? StateName,
    bool RemindersOn,
    DateOnly? SpecialPriceSavesDay,
    int SpecialPriceSavesCount,
    DateOnly? PlanVisibleSavesDay,
    int PlanVisibleSavesCount,
    PlanLimits Limits,
    DateOnly Today,
    long?[] Extras)
{
    public OwnerCalendarWindowValue Window => OwnerCalendarWindow.Of(Limits, Today);

    // Today's count of a daily counter (contract 4.3): a counter of another IST day reads 0.
    public static int CountToday(DateOnly? day, int count, DateOnly today) => day == today ? count : 0;

    public int SpecialPriceSavesToday => CountToday(SpecialPriceSavesDay, SpecialPriceSavesCount, Today);

    public int PlanVisibleSavesToday => CountToday(PlanVisibleSavesDay, PlanVisibleSavesCount, Today);

    public long? Extra(int slot) => slot >= 1 && slot <= Extras.Length ? Extras[slot - 1] : null;
}

// Contract 2.0 "Owner context": ONE statement = the caller's live listing of the kind LEFT JOIN his active plan row (and the district
// state); limits are then computed in memory from the cached catalog and payment flag. No listing = null (404 ListingNotFound).
// A lane that needs more numbers in the same statement passes up to four scalar subqueries aliased "X1".."X4" (listing alias l,
// parameter @me, @today) and reads them back with Extra(n), so its budget stays at one context statement.
public sealed class OwnerContextReader(ApplicationDbContext db, IPlanCatalogCache catalog, IPaymentFeatureReader payment, TimeProvider clock)
{
    public const int MaxExtras = 4;

    public sealed class Row
    {
        public Guid ListingId { get; set; }
        public string Status { get; set; } = string.Empty;
        public Guid? DistrictId { get; set; }
        public string? StateName { get; set; }
        public bool RemindersOn { get; set; }
        public DateOnly? SpecialPriceSavesDay { get; set; }
        public short SpecialPriceSavesCount { get; set; }
        public DateOnly? PlanVisibleSavesDay { get; set; }
        public short PlanVisibleSavesCount { get; set; }
        public Guid? UserPlanId { get; set; }
        public Guid? PlanId { get; set; }
        public string? Source { get; set; }
        public DateTime? StartsAt { get; set; }
        public DateTime? EndsAt { get; set; }
        public long? X1 { get; set; }
        public long? X2 { get; set; }
        public long? X3 { get; set; }
        public long? X4 { get; set; }
    }

    public static string Sql(string kind, IReadOnlyList<string>? extras = null)
    {
        if (extras is { Count: > MaxExtras }) throw new ArgumentOutOfRangeException(nameof(extras), "At most four extra columns.");
        var (table, savesDay, savesCount) = kind switch
        {
            ListingKinds.Stay => ("\"StayListings\"", "l.\"SpecialPriceSavesDay\"", "l.\"SpecialPriceSavesCount\""),
            ListingKinds.Taxi => ("\"TaxiListings\"", "NULL::date", "0::smallint"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown listing kind"),
        };
        var x = new string[MaxExtras];
        for (var i = 0; i < MaxExtras; i++)
            x[i] = extras != null && i < extras.Count ? $"({extras[i]})::bigint" : "NULL::bigint";
        return $"""
            SELECT l."Id" AS "ListingId", l."Status", l."DistrictId", d."StateName", l."RemindersOn",
                   {savesDay} AS "SpecialPriceSavesDay", {savesCount} AS "SpecialPriceSavesCount",
                   l."PlanVisibleSavesDay", l."PlanVisibleSavesCount",
                   up."Id" AS "UserPlanId", up."PlanId", up."Source", up."StartsAt", up."EndsAt",
                   {x[0]} AS "X1", {x[1]} AS "X2", {x[2]} AS "X3", {x[3]} AS "X4"
            FROM {table} l
            LEFT JOIN "Districts" d ON d."Id" = l."DistrictId"
            LEFT JOIN LATERAL (
                SELECT u."Id", u."PlanId", u."Source", u."StartsAt", u."EndsAt" FROM "UserPlans" u
                WHERE u."UserId" = @me AND u."FeatureKey" = @kind AND {PlanRowSql.EntitledSql("u")}
                ORDER BY u."EndsAt" DESC LIMIT 1) up ON true
            WHERE l."UserId" = @me AND l."IsDeleted" = false
            LIMIT 1
            """;
    }

    public async Task<OwnerContext?> LoadAsync(
        Guid userId, string kind, IReadOnlyList<string>? extras = null, IReadOnlyList<NpgsqlParameter>? extraParameters = null, CancellationToken ct = default)
    {
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        var parameters = new List<object>
        {
            new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = userId },
            new NpgsqlParameter("kind", NpgsqlDbType.Varchar) { Value = kind },
            new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = today },
        };
        if (extraParameters != null) parameters.AddRange(extraParameters);

        var rows = await db.Database.SqlQueryRaw<Row>(Sql(kind, extras), parameters.ToArray()).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        return Build(userId, kind, rows[0], snapshot, flag, today);
    }

    public static OwnerContext Build(Guid userId, string kind, Row row, PlanCatalogSnapshot snapshot, PaymentFeatureState flag, DateOnly today)
    {
        var active = row.UserPlanId is { } upId && row.PlanId is { } planId
            ? new PlanEntitlementService.ActiveRow(upId, userId, planId, row.Source ?? string.Empty, row.StartsAt ?? default, row.EndsAt ?? default)
            : null;
        var limits = PlanEntitlementService.Compute(snapshot, flag, kind, active);
        return new OwnerContext(
            userId, kind, row.ListingId, row.Status, row.DistrictId, row.StateName, row.RemindersOn,
            row.SpecialPriceSavesDay, row.SpecialPriceSavesCount, row.PlanVisibleSavesDay, row.PlanVisibleSavesCount,
            limits, today, [row.X1, row.X2, row.X3, row.X4]);
    }
}
