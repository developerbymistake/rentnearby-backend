using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner;

// Contract 4.1 level 5: the listing row FOR UPDATE, taken by every limit check and every write of the owner tables, after the
// Users row (level 2) and request / ring locks (level 4) and before any owner table row (level 6). Needs an open transaction.
public static class OwnerLocks
{
    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }

    // false = the listing does not exist or is deleted (the caller answers 404).
    public static Task<bool> LockStayAsync(ApplicationDbContext db, Guid stayListingId, CancellationToken ct = default)
        => LockAsync(db, "\"StayListings\"", stayListingId, ct);

    public static Task<bool> LockTaxiAsync(ApplicationDbContext db, Guid taxiListingId, CancellationToken ct = default)
        => LockAsync(db, "\"TaxiListings\"", taxiListingId, ct);

    public static Task<bool> LockListingAsync(ApplicationDbContext db, string kind, Guid listingId, CancellationToken ct = default) => kind switch
    {
        ListingKinds.Stay => LockStayAsync(db, listingId, ct),
        ListingKinds.Taxi => LockTaxiAsync(db, listingId, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown listing kind"),
    };

    private static async Task<bool> LockAsync(ApplicationDbContext db, string table, Guid id, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Owner listing locks require an open transaction.");
#pragma warning disable EF1002 // table is one of two fixed names above, the id is a parameter.
        var rows = await db.Database
            .SqlQueryRaw<IdRow>($"SELECT \"Id\" FROM {table} WHERE \"Id\" = {{0}} AND \"IsDeleted\" = false FOR UPDATE", id)
            .ToListAsync(ct);
#pragma warning restore EF1002
        return rows.Count == 1;
    }
}
