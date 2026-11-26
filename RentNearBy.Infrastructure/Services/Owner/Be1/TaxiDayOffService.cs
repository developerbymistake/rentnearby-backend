using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using static RentNearBy.Infrastructure.Services.Owner.Be1.OwnerCalendarSql;

namespace RentNearBy.Infrastructure.Services.Owner.Be1;

// Contract 1.3 A1 / A2. Read = the owner context + ONE state statement. Save = one READ COMMITTED transaction under the taxi row lock
// (the same row booking confirm and the calendar writer lock), the plan re-read under it, one flags statement over the set dates,
// one upsert, one delete, the state statement. All or nothing; the body is the wanted end state, so a repeat ends the same.
public sealed class TaxiDayOffService(ApplicationDbContext db, ILogger<TaxiDayOffService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string BookedOn(string day) => $"""
        EXISTS (SELECT 1 FROM "CalendarEntries" c WHERE c."ListingKind" = '{ListingKinds.Taxi}' AND c."ListingId" = @l
                AND c."Kind" = '{CalendarEntryKinds.Auto}' AND NOT c."Cancelled" AND c."DateFrom" <= {day} AND c."DateTo" >= {day})
        """;

    public static readonly string StateSql = $"""
        SELECT COALESCE((SELECT json_agg(json_build_object('date', d."Date", 'reason', d."Reason") ORDER BY d."Date")
                         FROM "TaxiDayOffs" d WHERE d."TaxiListingId" = @l AND d."Date" >= @today), '[]')::text AS "DaysOff",
               COALESCE((SELECT json_agg(DISTINCT g.day::date ORDER BY g.day::date)
                         FROM "CalendarEntries" c
                         CROSS JOIN LATERAL generate_series(GREATEST(c."DateFrom", @today)::timestamp, LEAST(c."DateTo", @to)::timestamp, interval '1 day') g(day)
                         WHERE c."ListingKind" = '{ListingKinds.Taxi}' AND c."ListingId" = @l AND c."Kind" = '{CalendarEntryKinds.Auto}' AND NOT c."Cancelled"
                           AND c."DateTo" >= @today AND c."DateFrom" <= @to), '[]')::text AS "BookedDays",
               (SELECT count(*) FROM "CalendarEntries" c
                WHERE c."ListingKind" = '{ListingKinds.Taxi}' AND c."ListingId" = @l AND c."Kind" = '{CalendarEntryKinds.Auto}' AND NOT c."Cancelled"
                  AND c."DateFrom" >= @m1 AND c."DateFrom" < @m2)::int AS "MonthBookings",
               (SELECT count(*) FROM "TaxiDayOffs" d WHERE d."TaxiListingId" = @l AND d."Date" >= @today AND d."Date" < @m2)::int AS "MonthDaysOff",
               l."IsAvailable" AS "SwitchOn"
        FROM "TaxiListings" l WHERE l."Id" = @l
        """;

    // Booked and already-off flags of every set date in one statement (contract 1.3).
    public static readonly string FlagsSql = $"""
        SELECT s.d AS "Date",
               EXISTS (SELECT 1 FROM "TaxiDayOffs" x WHERE x."TaxiListingId" = @l AND x."Date" = s.d) AS "Off",
               {BookedOn("s.d")} AS "Booked"
        FROM unnest(@setDates) AS s(d)
        """;

    public const string UpsertSql = """
        INSERT INTO "TaxiDayOffs" ("TaxiListingId", "Date", "Reason", "CreatedAt", "UpdatedAt")
        SELECT @l, x.d, x.r, now(), now() FROM unnest(@setDates, @reasons) AS x(d, r)
        ON CONFLICT ("TaxiListingId", "Date") DO UPDATE SET "Reason" = EXCLUDED."Reason", "UpdatedAt" = now()
        WHERE "TaxiDayOffs"."Reason" IS DISTINCT FROM EXCLUDED."Reason"
        """;

    public const string ClearSql = """DELETE FROM "TaxiDayOffs" WHERE "TaxiListingId" = @l AND "Date" = ANY(@clear)""";

    public async Task<TaxiDaysOffStateDto> ReadAsync(OwnerContext ctx, CancellationToken ct = default)
        => await StateAsync(ctx, ct) ?? Empty(ctx);

    public async Task<Be1Result<TaxiDaysOffStateDto>> SaveAsync(OwnerContext ctx, TaxiDayOffChanges changes, CancellationToken ct = default)
    {
        var past = TaxiDayOffRules.PastDates(changes, ctx.Today);
        if (past.Count > 0) return Be1Error.Of(OwnerErrorTypes.DayOffPast, new { dates = TaxiDayOffRules.Texts(past) });

        return await PgRetry.OnceAsync<Be1Result<TaxiDaysOffStateDto>>(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, c);
            if (!await OwnerLocks.LockTaxiAsync(db, ctx.ListingId, c)) return Be1Error.Of(OwnerErrorTypes.ListingNotFound);
            var locked = await OwnerContextReader.WithLimitsUnderLockAsync(db, ctx, c);

            if (changes.Set.Count > 0)
            {
                var dates = changes.Set.Select(s => s.Date).ToArray();
                var flags = await db.Database.SqlQueryRaw<FlagRow>(FlagsSql, Uuid("l", locked.ListingId), DateArray("setDates", dates)).ToListAsync(c);
                var off = flags.Where(f => f.Off).Select(f => f.Date).ToHashSet();
                var beyond = TaxiDayOffRules.BeyondWindow(dates.Where(d => !off.Contains(d)), locked.Window);
                if (beyond.Count > 0)
                    return locked.Limits.CanUpgrade
                        ? Be1Error.Of(OwnerErrorTypes.PlanFeatureLocked, new { key = PlanFeatureKeys.MonthsAhead })
                        : Be1Error.Of(OwnerErrorTypes.CalendarOutsideWindow);
                var booked = flags.Where(f => f.Booked).Select(f => f.Date).OrderBy(d => d).ToList();
                if (booked.Count > 0) return Be1Error.Of(OwnerErrorTypes.DayOffBooked, new { dates = TaxiDayOffRules.Texts(booked) });

                await db.Database.ExecuteSqlRawAsync(UpsertSql,
                    [Uuid("l", locked.ListingId), DateArray("setDates", dates), TextArray("reasons", changes.Set.Select(s => s.Reason).ToArray())], c);
            }
            if (changes.Clear.Count > 0)
                await db.Database.ExecuteSqlRawAsync(ClearSql, [Uuid("l", locked.ListingId), DateArray("clear", changes.Clear.ToArray())], c);

            var state = await StateAsync(locked, c);
            if (state == null) return Be1Error.Of(OwnerErrorTypes.ListingNotFound);
            await tx.CommitAsync(c);
            return Be1Result<TaxiDaysOffStateDto>.Success(state);
        }, ct, logger);
    }

    private async Task<TaxiDaysOffStateDto?> StateAsync(OwnerContext ctx, CancellationToken ct)
    {
        var (m1, m2) = TaxiDayOffRules.Month(ctx.Today);
        var window = ctx.Window;
        var rows = await db.Database.SqlQueryRaw<StateRow>(StateSql,
            Uuid("l", ctx.ListingId), Date("today", ctx.Today), Date("to", window.MonthTo), Date("m1", m1), Date("m2", m2)).ToListAsync(ct);
        if (rows.Count == 0) return null;
        return Map(ctx, rows[0]);
    }

    public static TaxiDaysOffStateDto Map(OwnerContext ctx, StateRow row)
    {
        var daysOff = Parse<List<DayOffJson>>(row.DaysOff)?.Where(d => d.Date != null)
            .Select(d => new TaxiDayOffDto(d.Date!.Value, d.Reason)).ToList() ?? [];
        var booked = Parse<List<DateOnly>>(row.BookedDays) ?? [];
        return new TaxiDaysOffStateDto(ctx.Today, WindowDto.From(ctx.Window), ctx.Limits.MonthsAhead, row.SwitchOn, daysOff, booked,
            new TaxiAvailabilityCountsDto(TaxiDayOffRules.MonthText(ctx.Today), row.MonthBookings, row.MonthDaysOff));
    }

    private static TaxiDaysOffStateDto Empty(OwnerContext ctx)
        => new(ctx.Today, WindowDto.From(ctx.Window), ctx.Limits.MonthsAhead, false, [], [],
            new TaxiAvailabilityCountsDto(TaxiDayOffRules.MonthText(ctx.Today), 0, 0));

    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static Npgsql.NpgsqlParameter DateArray(string name, DateOnly[] values)
        => new(name, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Date) { Value = values };

    private static Npgsql.NpgsqlParameter TextArray(string name, string?[] values)
        => new(name, NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = values };

    public sealed class StateRow
    {
        public string DaysOff { get; set; } = "[]";
        public string BookedDays { get; set; } = "[]";
        public int MonthBookings { get; set; }
        public int MonthDaysOff { get; set; }
        public bool SwitchOn { get; set; }
    }

    private sealed class FlagRow
    {
        public DateOnly Date { get; set; }
        public bool Off { get; set; }
        public bool Booked { get; set; }
    }

    private sealed class DayOffJson
    {
        public DateOnly? Date { get; set; }
        public string? Reason { get; set; }
    }
}
