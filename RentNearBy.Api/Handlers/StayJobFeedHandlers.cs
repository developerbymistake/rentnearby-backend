using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Repositories;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

public static class StayJobFeedHandlers
{
    public delegate Task<Dictionary<Guid, StayJobFeedItemDto>> Hydrate(IReadOnlyList<NearbyRow> rows, string? centreCityName);

    public static Task<IResult> GetFeed(
        double? lat, double? lng, long? cityKey, int? pageSize, string? pageToken,
        Guid? departmentId, int? minExperience, int? maxExperience, int? minSalary,
        INearbyFeedQuery query, INearbyContext ctx, ITripCityCatalog catalog, ICityResolver resolver,
        ApplicationDbContext db, CancellationToken ct = default)
        => BuildAsync(lat, lng, cityKey, pageSize, pageToken, departmentId, minExperience, maxExperience, minSalary,
            query, ctx, catalog, (rows, centre) => StayJobFeedHydrator.LoadAsync(db, rows, resolver, centre), ct);

    // Public so tests can drive the whole flow with a fake hydrator.
    public static async Task<IResult> BuildAsync(
        double? lat, double? lng, long? cityKey, int? pageSize, string? pageToken,
        Guid? departmentId, int? minExperience, int? maxExperience, int? minSalary,
        INearbyFeedQuery query, INearbyContext ctx, ITripCityCatalog catalog, Hydrate hydrate, CancellationToken ct = default)
    {
        if (cityKey is { } key)
        {
            var (available, city) = await catalog.FindAsync(key, ct);
            if (available)
            {
                if (city == null) return BadRequestResponse("Pick a valid city");
                lat = city.Latitude;
                lng = city.Longitude;
            }
            else if (!NearbyRules.ValidLatLng(lat, lng))
                return ErrorResponse(503, "Cities are not available right now", TripErrorTypes.CitiesUnavailable);
        }

        if (NearbyFeedShared.TryParse(lat, lng, pageSize, pageToken, out var args) is { } bad) return bad;

        var filter = new NearbyFilter(
            DepartmentId: departmentId,
            MinExperience: minExperience is { } mn ? Math.Clamp(mn, StayJobRules.MinExperienceYears, StayJobRules.MaxExperienceYears) : null,
            MaxExperience: maxExperience is { } mx ? Math.Clamp(mx, StayJobRules.MinExperienceYears, StayJobRules.MaxExperienceYears) : null,
            MinSalary: minSalary);

        var rows = await query.PageAsync(NearbyKind.StayJob, args.Lat, args.Lng, args.After, args.PageSize + 1, filter, ct);
        var (page, hasMore) = NearbyFeedShared.Split(rows, args.PageSize);

        double? nearestKm = page.Count > 0 ? NearbyRules.StayKm(page[0].DistanceMeters) : null;
        var header = await ctx.HeaderAsync(args.Lat, args.Lng, nearestKm, ct);

        var loaded = await hydrate(page, header.CentreCityName);
        var items = page.Where(r => loaded.ContainsKey(r.Id)).Select(r => loaded[r.Id]).ToList();

        return OkResponse(NearbyFeedShared.Compose(header, Array.Empty<StayJobFeedItemDto>(), items, page.Count > 0 ? page[^1] : null, hasMore));
    }
}
