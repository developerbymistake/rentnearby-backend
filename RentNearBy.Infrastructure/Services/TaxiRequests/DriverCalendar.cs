using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

public sealed record DriverLockRow(Guid Id, Guid UserId, string Status, bool IsDeleted)
{
    public bool IsBookable => !IsDeleted && Status == TaxiStatuses.Approved;
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

    // Read side (datesConflict on Selected offers): same query, no lock, no transaction needed.
    public static Task<TaxiDateRange?> ConflictForAsync(ApplicationDbContext db, Guid driverListingId, DateOnly from, DateOnly to, CancellationToken ct = default)
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
        SELECT "Id", "UserId", "Status", "IsDeleted" FROM "TaxiListings" WHERE "Id" = @driver FOR UPDATE
        """;

    public static readonly string OverlapSql = $"""
        SELECT x."From", x."To" FROM (
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
        ) x
        ORDER BY x."From", x."To"
        LIMIT 1
        """;
}

public sealed class EfDriverCalendarSql(ApplicationDbContext db) : IDriverCalendarSql
{
    public async Task<DriverLockRow?> LockDriverAsync(Guid driverListingId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<LockRow>(DriverCalendar.LockDriverSql,
            new NpgsqlParameter("driver", NpgsqlDbType.Uuid) { Value = driverListingId }).ToListAsync(ct);
        return rows.Count == 0 ? null : new DriverLockRow(rows[0].Id, rows[0].UserId, rows[0].Status, rows[0].IsDeleted);
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

    private sealed class LockRow
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string Status { get; set; } = "";
        public bool IsDeleted { get; set; }
    }

    private sealed class RangeRow
    {
        public DateOnly From { get; set; }
        public DateOnly To { get; set; }
    }
}
