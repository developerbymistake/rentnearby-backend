using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

public sealed record TaxiPickupPlace(long CityKey, string Name, int Drivers);

public sealed record TaxiPickupLists(IReadOnlyList<TaxiPickupPlace> Pickup, IReadOnlyList<TaxiPickupPlace> Drop, int SightseeingDrivers)
{
    public IReadOnlyList<long> PickupKeys => Pickup.Select(p => p.CityKey).ToList();
    public IReadOnlyList<long> DropKeys => Drop.Select(p => p.CityKey).ToList();
    public string? PickupName(long? key) => key is { } k ? Pickup.FirstOrDefault(p => p.CityKey == k)?.Name : null;
    public string? DropName(long? key) => key is { } k ? Drop.FirstOrDefault(p => p.CityKey == k)?.Name : null;
}

// Contract T1 lists from driver routes by city key (decisions 12, 13, 23e): pickup = FROM cities of routes whose TO city is
// the trip's first city, drop = the same around the last city, a FROM equal to the TO city is not listed, drivers counted
// by the pool rule of MASTER_PLAN 2.5. City names come from the cached catalog (no Atlas call per request); a key that is
// not a live city is left out, so the list and the create check always agree. Run outside any transaction.
public sealed class TaxiPickupPlaces(ApplicationDbContext db, ICityResolver resolver)
{
    public static readonly string FromCitiesSql = $"""
        SELECT rt."FromCityKey" AS "CityKey", count(DISTINCT l."Id")::int AS "Drivers"
        FROM "TaxiRoutes" rt
        JOIN "TaxiListings" l ON l."Id" = rt."TaxiListingId"
        WHERE rt."ToCityKey" = @to AND rt."FromCityKey" IS NOT NULL AND rt."FromCityKey" <> rt."ToCityKey"
        AND {TaxiCandidateQuery.StaticRule}
        GROUP BY rt."FromCityKey"
        """;

    public static readonly string DriversToCitySql = $"""
        SELECT count(DISTINCT l."Id")::int AS "Value"
        FROM "TaxiRoutes" rt
        JOIN "TaxiListings" l ON l."Id" = rt."TaxiListingId"
        WHERE rt."ToCityKey" = @to
        AND {TaxiCandidateQuery.StaticRule}
        """;

    public async Task<TaxiPickupLists> GetAsync(long firstCityKey, long lastCityKey, CancellationToken ct = default)
    {
        var pickupRows = await FromCitiesAsync(firstCityKey, ct);
        var dropRows = lastCityKey == firstCityKey ? pickupRows : await FromCitiesAsync(lastCityKey, ct);
        var sightseeing = await CountForAsync(firstCityKey, ct);
        return await BuildAsync(resolver, pickupRows, dropRows, sightseeing);
    }

    // Names the rows from the catalog; a key that is not a live city is left out of both lists.
    internal static async Task<TaxiPickupLists> BuildAsync(ICityResolver resolver, IReadOnlyList<FromCityRow> pickupRows,
        IReadOnlyList<FromCityRow> dropRows, int sightseeing)
    {
        var names = await NamesAsync(resolver, pickupRows.Concat(dropRows).Select(r => r.CityKey).Distinct());
        return new TaxiPickupLists(Named(pickupRows, names), Named(dropRows, names), sightseeing);
    }

    // Pool drivers with a route to this city from anywhere (sightseeing only, P18).
    public async Task<int> CountForAsync(long toCityKey, CancellationToken ct = default)
        => await db.Database.SqlQueryRaw<int>(DriversToCitySql, To(toCityKey)).SingleAsync(ct);

    // The single invalidation call for a change that moves the lists (routes, availability, readiness, removal).
    // No cache exists yet (B3-1 adds it), so there is nothing to bump.
    public Task BumpAsync() => Task.CompletedTask;

    private async Task<List<FromCityRow>> FromCitiesAsync(long toCityKey, CancellationToken ct)
        => await db.Database.SqlQueryRaw<FromCityRow>(FromCitiesSql, To(toCityKey)).ToListAsync(ct);

    private static async Task<Dictionary<long, string>> NamesAsync(ICityResolver resolver, IEnumerable<long> keys)
    {
        var names = new Dictionary<long, string>();
        foreach (var key in keys)
        {
            var pin = await resolver.FindAsync(key);
            if (pin is { Available: true, City.Name: { Length: > 0 } name }) names[key] = name;
        }
        return names;
    }

    private static List<TaxiPickupPlace> Named(IEnumerable<FromCityRow> rows, IReadOnlyDictionary<long, string> names)
        => rows.Where(r => names.ContainsKey(r.CityKey))
            .Select(r => new TaxiPickupPlace(r.CityKey, names[r.CityKey], r.Drivers))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.CityKey)
            .ToList();

    private static NpgsqlParameter To(long cityKey) => new("to", NpgsqlDbType.Bigint) { Value = cityKey };

    internal sealed class FromCityRow
    {
        public long CityKey { get; set; }
        public int Drivers { get; set; }
    }
}
