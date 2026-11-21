using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Owner.Be1;

// Contract 6.2 step 6, AU-25, AU-26: inside the caller's transaction, one set-based statement per owner table, in this order:
// calendar entries with their money notes, blocked ranges, special prices, weekly stats. Soft stay delete keeps the listing row,
// so blocks and special prices are deleted here and not left to the StayListings cascade.
public sealed class OwnerListingPurge(ApplicationDbContext db) : IOwnerListingPurge
{
    public const string CalendarEntriesSql = """DELETE FROM "CalendarEntries" WHERE "ListingKind" = @k AND "ListingId" = ANY(@ids)""";
    public const string StayBlockedRangesSql = """DELETE FROM "StayBlockedRanges" WHERE "StayListingId" = ANY(@ids)""";
    public const string StaySpecialPricesSql = """DELETE FROM "StaySpecialPrices" WHERE "StayListingId" = ANY(@ids)""";
    public const string OwnerWeeklyStatsSql = """DELETE FROM "OwnerWeeklyStats" WHERE "ListingKind" = @k AND "ListingId" = ANY(@ids)""";

    public static IReadOnlyList<string> Statements(string listingKind) => listingKind == ListingKinds.Stay
        ? [CalendarEntriesSql, StayBlockedRangesSql, StaySpecialPricesSql, OwnerWeeklyStatsSql]
        : [CalendarEntriesSql, OwnerWeeklyStatsSql];

    public async Task PurgeAsync(string listingKind, IReadOnlyCollection<Guid> listingIds, CancellationToken ct = default)
    {
        if (listingIds.Count == 0) return;
        if (!(listingKind is ListingKinds.Stay or ListingKinds.Taxi)) throw new ArgumentOutOfRangeException(nameof(listingKind), listingKind, "Unknown listing kind");
        var ids = listingIds.Distinct().ToArray();

        await RunAsync(CalendarEntriesSql, listingKind, ids, ct);
        if (listingKind == ListingKinds.Stay)
        {
            await RunAsync(StayBlockedRangesSql, listingKind, ids, ct);
            await RunAsync(StaySpecialPricesSql, listingKind, ids, ct);
        }
        await RunAsync(OwnerWeeklyStatsSql, listingKind, ids, ct);
    }

    private Task<int> RunAsync(string sql, string kind, Guid[] ids, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(sql,
        [
            new NpgsqlParameter("k", NpgsqlDbType.Varchar) { Value = kind },
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids },
        ], ct);
}
