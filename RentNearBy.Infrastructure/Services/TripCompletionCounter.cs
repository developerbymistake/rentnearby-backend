using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// The only writer of Users.CompletedTripsCount and Users.CompletedBookingsCount (owner feature, D-03..D-06, D-36).
// Each method is ONE statement: a data-modifying CTE claims the once-per-row guard (CompletionCountedAt IS NULL) and the
// outer UPDATE adds to the counter only for the rows it claimed. No read-modify-write, so a retry or a parallel call
// claims nothing and adds nothing. Every value is bound. Callers run it inside the completing transaction, after the
// Completed status write; the counter rolls back with it.
//
// Lock order: Trip row (held by both trip callers) -> TripEnquiries rows -> Users rows, Users last. Taxi: request lock
// (held) -> booking -> listing (RidesCompleted) -> Users. The Users update touches no key column, so it never waits on
// the KEY SHARE locks of the owner calendar writers. A rare deadlock with an account deletion is resolved by the
// deletion's single retry.
//
// Nothing else changes the counters: trip delete, retention clean-up, role change, leaving a trip and the deletion of
// another account keep them (guarded by CompletionCounterGuardTests).
public static class TripCompletionCounter
{
    // Seeker counter (D-03, D-04). Returns 1 when the trip was counted now, else 0.
    internal const string TripSql = """
        WITH c AS (
          UPDATE "Trips" t SET "CompletionCountedAt" = now()
          WHERE t."Id" = @trip AND t."Status" = @completed
            AND t."CompletionCountedAt" IS NULL
            AND t."EndDate" IS NOT NULL AND t."EndDate" < @today
            AND ( EXISTS (SELECT 1 FROM "TripEnquiries" e WHERE e."TripId" = t."Id" AND e."Status" = @confirmed)
               OR EXISTS (SELECT 1 FROM "TaxiBookings" b WHERE b."TripId" = t."Id" AND b."TravellerUserId" = t."OwnerUserId"
                          AND b."Status" IN (@booked, @inProgress, @bookingCompleted)) )
          RETURNING t."OwnerUserId")
        UPDATE "Users" u SET "CompletedTripsCount" = u."CompletedTripsCount" + 1
        FROM c WHERE u."Id" = c."OwnerUserId"
        """;

    // Stay Owner counter, plus a driver's confirmed taxi lead (D-05): one statement for every host of the trip.
    internal const string HostsSql = """
        WITH c AS (
          UPDATE "TripEnquiries" e SET "CompletionCountedAt" = now()
          WHERE e."TripId" = @trip AND e."Status" = @confirmed AND e."CompletionCountedAt" IS NULL
            AND e."CheckOut" IS NOT NULL AND e."CheckOut" < @today
            AND EXISTS (SELECT 1 FROM "Trips" t WHERE t."Id" = e."TripId" AND t."Status" = @completed)
          RETURNING e."OwnerUserId"),
        g AS (SELECT "OwnerUserId", count(*)::int AS n FROM c WHERE "OwnerUserId" IS NOT NULL GROUP BY "OwnerUserId")
        UPDATE "Users" u SET "CompletedBookingsCount" = u."CompletedBookingsCount" + g.n
        FROM g WHERE u."Id" = g."OwnerUserId"
        """;

    // Taxi Driver counter (D-06): a Completed booking whose ride was started in the app.
    internal const string TaxiBookingSql = """
        WITH c AS (
          UPDATE "TaxiBookings" b SET "CompletionCountedAt" = now()
          WHERE b."Id" = @booking AND b."Status" = @bookingCompleted AND b."CompletionCountedAt" IS NULL
            AND b."StartedAt" IS NOT NULL
          RETURNING b."DriverUserId")
        UPDATE "Users" u SET "CompletedBookingsCount" = u."CompletedBookingsCount" + 1
        FROM c WHERE u."Id" = c."DriverUserId"
        """;

    // Both trip statements, in the documented lock order. Called right after the trip's Completed write.
    public static async Task CountCompletedTripInTxAsync(ApplicationDbContext db, Guid tripId, DateOnly todayIst, CancellationToken ct = default)
    {
        await CountTripInTxAsync(db, tripId, todayIst, ct);
        await CountHostsInTxAsync(db, tripId, todayIst, ct);
    }

    public static Task<int> CountTripInTxAsync(ApplicationDbContext db, Guid tripId, DateOnly todayIst, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(TripSql,
            [
                Uuid("trip", tripId), Date("today", todayIst), Text("completed", TripStatuses.Completed),
                Text("confirmed", TripEnquiryStatuses.ConfirmedByBoth), Text("booked", BookingStates.Booked),
                Text("inProgress", BookingStates.InProgress), Text("bookingCompleted", BookingStates.Completed),
            ], ct);

    public static Task<int> CountHostsInTxAsync(ApplicationDbContext db, Guid tripId, DateOnly todayIst, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(HostsSql,
            [
                Uuid("trip", tripId), Date("today", todayIst), Text("completed", TripStatuses.Completed),
                Text("confirmed", TripEnquiryStatuses.ConfirmedByBoth),
            ], ct);

    public static Task<int> CountTaxiBookingInTxAsync(ApplicationDbContext db, Guid bookingId, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(TaxiBookingSql,
            [Uuid("booking", bookingId), Text("bookingCompleted", BookingStates.Completed)], ct);

    private static NpgsqlParameter Uuid(string name, Guid value) => new(name, NpgsqlDbType.Uuid) { Value = value };
    private static NpgsqlParameter Date(string name, DateOnly value) => new(name, NpgsqlDbType.Date) { Value = value };
    private static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Text) { Value = value };
}
