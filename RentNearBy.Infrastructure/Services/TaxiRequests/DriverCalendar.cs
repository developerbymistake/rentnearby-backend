using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

public sealed record DriverLockRow(Guid Id, Guid UserId, string Status, bool IsDeleted, bool EverApproved = false)
{
    // Confirm of an existing selection (D-15): a once-approved taxi under a vehicle re-review still confirms.
    public bool IsBookable => TaxiBookableRules.CanSelectExisting(Status, IsDeleted, EverApproved);
}

public enum DriverCheckOutcome
{
    Free,
    DriverMissing,
    Overlap,
}

public sealed record DriverCheck(DriverCheckOutcome Outcome, DriverLockRow? Driver, TaxiDateRange? Clash);

// The two statements of the P1 double booking guarantee. Both run on the caller's connection and transaction.
public interface IDriverCalendarSql
{
    Task<DriverLockRow?> LockDriverAsync(Guid driverListingId, CancellationToken ct);
    Task<TaxiDateRange?> FindOverlapAsync(Guid driverListingId, TaxiDateRange range, Guid? exceptEnquiryId, CancellationToken ct);
}

// A driver's day off or hiding manual booking inside the range is a clash too (SPEC 4.3, I-25): confirm answers DatesAlreadyBooked and the banner shows.
// MASTER_PLAN P1 and B2-6: every writer that can book a driver opens a READ COMMITTED transaction, locks the driver's
// TaxiListings row FOR UPDATE and only then sends a fresh overlap query over active TaxiBookings and confirmed legacy
// local taxi rows. Under READ COMMITTED each statement takes a new snapshot, so the query sees every booking committed
// by a writer that held the lock before us.
public static class DriverCalendar
{
    public const IsolationLevel ConfirmIsolation = IsolationLevel.ReadCommitted;

    public static Task<IDbContextTransaction> BeginConfirmTransactionAsync(ApplicationDbContext db, CancellationToken ct = default)
        => db.Database.BeginTransactionAsync(ConfirmIsolation, ct);

    public static async Task<DriverCheck> LockAndCheckAsync(IDriverCalendarSql sql, Guid driverListingId, TaxiDateRange range,
        Guid? exceptEnquiryId, CancellationToken ct)
    {
        if (!range.IsValid) throw new ArgumentException("Range must not end before it starts", nameof(range));
        var driver = await sql.LockDriverAsync(driverListingId, ct);
        if (driver is null) return new DriverCheck(DriverCheckOutcome.DriverMissing, null, null);
        var clash = await sql.FindOverlapAsync(driverListingId, range, exceptEnquiryId, ct);
        return clash is { } c
            ? new DriverCheck(DriverCheckOutcome.Overlap, driver, c)
            : new DriverCheck(DriverCheckOutcome.Free, driver, null);
    }

    // Accept (D3) and select (T7): bookings only. A day off or a hiding manual row is refused at confirm alone (contract 1.8, R16).
    public static Task<TaxiDateRange?> ConflictForAsync(ApplicationDbContext db, Guid driverListingId, DateOnly from, DateOnly to, CancellationToken ct = default)
        => new EfDriverCalendarSql(db).FindBookingOverlapAsync(driverListingId, new TaxiDateRange(from, to), ct);

    // Read side (datesConflict on Selected offers): the confirm query, so the banner shows what confirm will refuse.
    public static Task<TaxiDateRange?> SelectedConflictForAsync(ApplicationDbContext db, Guid driverListingId, DateOnly from, DateOnly to, CancellationToken ct = default)
        => new EfDriverCalendarSql(db).FindOverlapAsync(driverListingId, new TaxiDateRange(from, to), null, ct);

    public static bool Overlaps(TaxiDateRange a, TaxiDateRange b) => a.Overlaps(b);

    // A legacy local taxi row stores CheckIn = first day and CheckOut = last day + 1; a row without CheckOut covers CheckIn.
    public static TaxiDateRange? LegacyRange(DateOnly? checkIn, DateOnly? checkOut)
    {
        if (checkIn is not { } from) return null;
        var to = checkOut is { } end && end > from ? end.AddDays(-1) : from;
        return new TaxiDateRange(from, to);
    }

    public const string LockDriverSql = """
        SELECT "Id", "UserId", "Status", "IsDeleted", "EverApproved" FROM "TaxiListings" WHERE "Id" = @driver FOR UPDATE
        """;

    private static readonly string BookingRows = $"""
            SELECT b."DateFrom" AS "From", b."DateTo" AS "To"
            FROM "TaxiBookings" b
            WHERE b."DriverListingId" = @driver AND b."Status" IN ({BookingStates.ActiveSqlList})
              AND b."DateFrom" <= @to AND b."DateTo" >= @from
            UNION ALL
            SELECT e."CheckIn" AS "From", (COALESCE(e."CheckOut", e."CheckIn" + 1) - 1) AS "To"
            FROM "TripEnquiries" e
            WHERE e."ListingId" = @driver AND e."Kind" = '{TripEnquiryKinds.Taxi}' AND e."TripId" IS NOT NULL AND e."Day" IS NULL
              AND e."Status" = '{TripEnquiryStatuses.ConfirmedByBoth}' AND e."CheckIn" IS NOT NULL
              AND e."CheckIn" <= @to AND COALESCE(e."CheckOut", e."CheckIn" + 1) > @from
              AND (@exceptEnquiry IS NULL OR e."Id" <> @exceptEnquiry)
        """;

    private const string DayOffRows = """
            SELECT d."Date" AS "From", d."Date" AS "To"
            FROM "TaxiDayOffs" d
            WHERE d."TaxiListingId" = @driver AND d."Date" BETWEEN @from AND @to
        """;

    // I-25: a driver's own booking without KeepVisible (a phone booking) makes him busy on its days, like a day off.
    private static readonly string ManualRows = $"""
            SELECT c."DateFrom" AS "From", c."DateTo" AS "To"
            FROM "CalendarEntries" c
            WHERE c."ListingKind" = '{ListingKinds.Taxi}' AND c."ListingId" = @driver AND {TaxiAvailabilitySql.ManualHiding("c")}
              AND c."DateFrom" <= @to AND c."DateTo" >= @from
        """;

    private static string FirstClash(string rows) => $"""
        SELECT x."From", x."To" FROM (
        {rows}
        ) x
        ORDER BY x."From", x."To"
        LIMIT 1
        """;

    // Confirm (P1 lock) and the Selected banner: bookings, confirmed legacy rows, days off and hiding manual rows.
    public static readonly string OverlapSql = FirstClash(BookingRows + "\n    UNION ALL\n" + DayOffRows + "\n    UNION ALL\n" + ManualRows);

    // Accept and select: bookings and confirmed legacy rows only.
    public static readonly string BookingOverlapSql = FirstClash(BookingRows);
}

public sealed class EfDriverCalendarSql(ApplicationDbContext db) : IDriverCalendarSql
{
    public async Task<DriverLockRow?> LockDriverAsync(Guid driverListingId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<LockRow>(DriverCalendar.LockDriverSql,
            new NpgsqlParameter("driver", NpgsqlDbType.Uuid) { Value = driverListingId }).ToListAsync(ct);
        return rows.Count == 0 ? null : new DriverLockRow(rows[0].Id, rows[0].UserId, rows[0].Status, rows[0].IsDeleted, rows[0].EverApproved);
    }

    public async Task<TaxiDateRange?> FindOverlapAsync(Guid driverListingId, TaxiDateRange range, Guid? exceptEnquiryId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<RangeRow>(DriverCalendar.OverlapSql,
            new NpgsqlParameter("driver", NpgsqlDbType.Uuid) { Value = driverListingId },
            new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = range.From },
            new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = range.To },
            new NpgsqlParameter("exceptEnquiry", NpgsqlDbType.Uuid) { Value = (object?)exceptEnquiryId ?? DBNull.Value })
            .ToListAsync(ct);
        return rows.Count == 0 ? null : new TaxiDateRange(rows[0].From, rows[0].To);
    }

    public async Task<TaxiDateRange?> FindBookingOverlapAsync(Guid driverListingId, TaxiDateRange range, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<RangeRow>(DriverCalendar.BookingOverlapSql,
            new NpgsqlParameter("driver", NpgsqlDbType.Uuid) { Value = driverListingId },
            new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = range.From },
            new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = range.To },
            new NpgsqlParameter("exceptEnquiry", NpgsqlDbType.Uuid) { Value = DBNull.Value })
            .ToListAsync(ct);
        return rows.Count == 0 ? null : new TaxiDateRange(rows[0].From, rows[0].To);
    }

    private sealed class LockRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string Status { get; set; } = "";
        public bool IsDeleted { get; set; }
        public bool EverApproved { get; set; }
    }

    private sealed class RangeRow
    {
        public DateOnly From { get; set; }
        public DateOnly To { get; set; }
    }
}
