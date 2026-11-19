using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using StackExchange.Redis;

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
// by the list rule (registered drivers; ring readiness is the matcher's). City names come from the cached catalog (no Atlas call per request); a key that is
// not a live city is left out, so the list and the create check always agree. Run outside any transaction.
public sealed class TaxiPickupPlaces
{
    private readonly ApplicationDbContext db;
    private readonly ICityResolver resolver;
    private readonly TaxiPickupCache _cache;

    // Redis is optional (no REDIS_URL, no multiplexer): then every call goes to the database.
    public TaxiPickupPlaces(ApplicationDbContext db, ICityResolver resolver, IConnectionMultiplexer? redis = null, ILogger<TaxiPickupPlaces>? logger = null)
        : this(db, resolver, redis is null ? null : new RedisPickupCacheStore(redis), logger)
    {
    }

    internal TaxiPickupPlaces(ApplicationDbContext db, ICityResolver resolver, ITaxiPickupCacheStore? store, ILogger? logger = null)
    {
        this.db = db;
        this.resolver = resolver;
        _cache = new TaxiPickupCache(store, logger);
    }

    public static readonly string FromCitiesSql = $"""
        SELECT rt."FromCityKey" AS "CityKey", count(DISTINCT l."Id")::int AS "Drivers"
        FROM "TaxiRoutes" rt
        JOIN "TaxiListings" l ON l."Id" = rt."TaxiListingId"
        WHERE rt."ToCityKey" = @to AND rt."FromCityKey" IS NOT NULL AND rt."FromCityKey" <> rt."ToCityKey"
        AND {TaxiCandidateQuery.ListRule}
        GROUP BY rt."FromCityKey"
        """;

    public static readonly string DriversToCitySql = $"""
        SELECT count(DISTINCT l."Id")::int AS "Value"
        FROM "TaxiRoutes" rt
        JOIN "TaxiListings" l ON l."Id" = rt."TaxiListingId"
        WHERE rt."ToCityKey" = @to
        AND {TaxiCandidateQuery.ListRule}
        """;

    public async Task<TaxiPickupLists> GetAsync(long firstCityKey, long lastCityKey, CancellationToken ct = default)
    {
        var pickupRows = await FromCitiesAsync(firstCityKey, ct);
        var dropRows = lastCityKey == firstCityKey ? pickupRows : await FromCitiesAsync(lastCityKey, ct);
        var sightseeing = await CountForAsync(firstCityKey, ct);
        return await BuildAsync(resolver, pickupRows, dropRows, sightseeing, firstCityKey, lastCityKey);
    }

    // T1 body. Paused = the master switch is off; seats follow the traveller count.
    public static PickupPlacesDto ToDto(TaxiPickupLists lists, TaxiCityRefDto? firstCity, TaxiCityRefDto? lastCity, bool switchOn, int travellers,
        List<TaxiSightseeingCityDto>? sightseeingCities = null)
        => new()
        {
            Paused = !switchOn,
            FirstCity = firstCity,
            LastCity = lastCity,
            Pickup = lists.Pickup.Select(ToDto).ToList(),
            Drop = lists.Drop.Select(ToDto).ToList(),
            SightseeingDrivers = lists.SightseeingDrivers,
            Travellers = travellers,
            SuggestedSeats = TaxiRequestSeats.SuggestedFor(travellers),
            SightseeingCities = sightseeingCities,
            SeatOptions = TaxiRequestSeats.Allowed.ToList(),
            MaxTravellers = TaxiRequestSeats.MaxTravellers,
        };

    // O4: the sightseeing pool of every plan city (the first city reuses the count already read for the lists).
    public async Task<List<TaxiSightseeingCityDto>> SightseeingCitiesAsync(TripPlan plan, long firstCityKey, int firstCityDrivers, CancellationToken ct = default)
    {
        var result = new List<TaxiSightseeingCityDto>();
        foreach (var (key, name, days) in TaxiRequestCreateRules.SightseeingCityDays(plan, firstCityKey))
        {
            var drivers = key == firstCityKey ? firstCityDrivers : await CountForAsync(key, ct);
            result.Add(new TaxiSightseeingCityDto { CityKey = key, Name = name, Days = days, Drivers = drivers });
        }
        return result;
    }

    private static TaxiPickupPlaceDto ToDto(TaxiPickupPlace p) => new() { CityKey = p.CityKey, Name = p.Name, Drivers = p.Drivers };

    // Names the rows from the catalog; a key that is not a live city is left out of both lists. With the trip's first and
    // last city the lists are sorted by distance from them (contract T1), then name; without, by name.
    internal static async Task<TaxiPickupLists> BuildAsync(ICityResolver resolver, IReadOnlyList<FromCityRow> pickupRows,
        IReadOnlyList<FromCityRow> dropRows, int sightseeing, long? firstCityKey = null, long? lastCityKey = null)
    {
        var points = await PointsAsync(resolver, pickupRows.Concat(dropRows).Select(r => r.CityKey).Distinct());
        var firstAt = firstCityKey is { } f ? (await resolver.FindAsync(f)).City : null;
        var lastAt = lastCityKey is { } l ? (await resolver.FindAsync(l)).City : null;
        return new TaxiPickupLists(Named(pickupRows, points, firstAt), Named(dropRows, points, lastAt), sightseeing);
    }

    // Listed drivers with a route to this city from anywhere (sightseeing only, P18).
    public async Task<int> CountForAsync(long toCityKey, CancellationToken ct = default)
        => await _cache.GetOrLoadAsync("cnt", toCityKey,
            async () => await db.Database.SqlQueryRaw<int>(DriversToCitySql, To(toCityKey)).SingleAsync(ct));

    // The single invalidation call for a change that moves the lists (routes, availability, readiness, removal): it moves
    // the cache generation, so every cached entry is ignored at once. Never throws; without Redis there is nothing to move.
    public Task BumpAsync() => _cache.BumpAsync();

    public static Task BumpAsync(IConnectionMultiplexer? redis)
        => new TaxiPickupCache(redis is null ? null : new RedisPickupCacheStore(redis)).BumpAsync();

    private async Task<List<FromCityRow>> FromCitiesAsync(long toCityKey, CancellationToken ct)
        => await _cache.GetOrLoadAsync("from", toCityKey,
            async () => await db.Database.SqlQueryRaw<FromCityRow>(FromCitiesSql, To(toCityKey)).ToListAsync(ct));

    private static async Task<Dictionary<long, TripCityInfo>> PointsAsync(ICityResolver resolver, IEnumerable<long> keys)
    {
        var cities = new Dictionary<long, TripCityInfo>();
        foreach (var key in keys)
        {
            var pin = await resolver.FindAsync(key);
            if (pin is { Available: true, City.Name: { Length: > 0 } } && pin.City is { } city) cities[key] = city;
        }
        return cities;
    }

    private static List<TaxiPickupPlace> Named(IEnumerable<FromCityRow> rows, IReadOnlyDictionary<long, TripCityInfo> cities, TripCityInfo? anchor)
        => rows.Where(r => cities.ContainsKey(r.CityKey))
            .Select(r => (Place: new TaxiPickupPlace(r.CityKey, cities[r.CityKey].Name, r.Drivers), City: cities[r.CityKey]))
            .OrderBy(x => anchor is null ? 0 : GeoMath.HaversineKm(anchor.Latitude, anchor.Longitude, x.City.Latitude, x.City.Longitude))
            .ThenBy(x => x.Place.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Place.CityKey)
            .Select(x => x.Place)
            .ToList();

    private static NpgsqlParameter To(long cityKey) => new("to", NpgsqlDbType.Bigint) { Value = cityKey };

    internal sealed class FromCityRow
    {
        public long CityKey { get; set; }
        public int Drivers { get; set; }
    }
}
