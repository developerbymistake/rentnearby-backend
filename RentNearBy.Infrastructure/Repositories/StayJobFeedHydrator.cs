using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public static class StayJobFeedHydrator
{
    // Rows are job ids with the stay distance. Input order is kept; a missing id is skipped.
    // Stay coordinates are read only to find the Atlas city and are never returned.
    public static async Task<Dictionary<Guid, StayJobFeedItemDto>> LoadAsync(
        ApplicationDbContext db, IReadOnlyList<NearbyRow> rows, ICityResolver resolver, string? centreCityName)
    {
        var result = new Dictionary<Guid, StayJobFeedItemDto>();
        if (rows.Count == 0) return result;
        var ids = rows.Select(r => r.Id).ToList();

        var loaded = await (
            from j in db.StayJobs.AsNoTracking()
            where ids.Contains(j.Id)
            join s in db.StayListings.AsNoTracking() on j.StayListingId equals s.Id
            select new
            {
                Item = new StayJobFeedItemDto
                {
                    Id = j.Id,
                    StayListingId = s.Id,
                    StayName = s.Name,
                    StayTypeName = s.StayType != null ? s.StayType.Name : null,
                    CoverPhotoUrl = s.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
                    Title = j.Title,
                    DepartmentName = j.Department.Name,
                    RoleName = j.Role.Name,
                    JobTypeName = j.JobType.Name,
                    ShiftTypeName = j.ShiftType.Name,
                    SalaryTypeName = j.SalaryType.Name,
                    MinimumExperienceYears = j.MinimumExperienceYears,
                    MinSalary = j.MinSalary,
                    MaxSalary = j.MaxSalary,
                    DistrictId = s.DistrictId,
                    DistrictName = s.District != null ? s.District.Name : null,
                    StateName = s.District != null ? s.District.StateName : null,
                    CreatedAt = j.CreatedAt,
                },
                s.Latitude,
                s.Longitude,
            }).ToListAsync();

        return await AssembleAsync(rows, loaded.Select(x => (x.Item, x.Latitude, x.Longitude)).ToList(), resolver, centreCityName);
    }

    // Input row order is kept; a missing id is skipped; a stay without coordinates gets no city.
    internal static async Task<Dictionary<Guid, StayJobFeedItemDto>> AssembleAsync(
        IReadOnlyList<NearbyRow> rows, IReadOnlyList<(StayJobFeedItemDto Item, decimal? Latitude, decimal? Longitude)> loaded,
        ICityResolver resolver, string? centreCityName)
    {
        var result = new Dictionary<Guid, StayJobFeedItemDto>();
        var byId = loaded.ToDictionary(x => x.Item.Id);
        var cities = await ResolveCitiesAsync(
            loaded.Where(x => x.Latitude != null && x.Longitude != null).Select(x => (x.Latitude!.Value, x.Longitude!.Value)), resolver);
        foreach (var row in rows)
        {
            if (!byId.TryGetValue(row.Id, out var x) || result.ContainsKey(row.Id)) continue;
            var city = x.Latitude is { } la && x.Longitude is { } lo ? cities[(la, lo)] : null;
            Fill(x.Item, row.DistanceMeters, city, centreCityName);
            result[row.Id] = x.Item;
        }
        return result;
    }

    private const int ResolveParallel = 8;

    // Each distinct stay point is resolved once, a few at a time; the first failure propagates.
    internal static async Task<Dictionary<(decimal Lat, decimal Lng), TripCityInfo?>> ResolveCitiesAsync(
        IEnumerable<(decimal Lat, decimal Lng)> points, ICityResolver resolver)
    {
        var distinct = points.Distinct().ToList();
        var map = new Dictionary<(decimal Lat, decimal Lng), TripCityInfo?>(distinct.Count);
        if (distinct.Count == 0) return map;

        using var gate = new SemaphoreSlim(ResolveParallel);
        var pins = await Task.WhenAll(distinct.Select(async p =>
        {
            await gate.WaitAsync();
            try { return (await resolver.ResolveAsync((double)p.Lat, (double)p.Lng)).City; }
            finally { gate.Release(); }
        }));
        for (var i = 0; i < distinct.Count; i++) map[distinct[i]] = pins[i];
        return map;
    }

    // City null: no village or district-made city name, only the district shows.
    public static void Fill(StayJobFeedItemDto item, double distanceMeters, TripCityInfo? city, string? centreCityName)
    {
        var km = NearbyRules.StayKm(distanceMeters);
        item.CityKey = city?.CityKey;
        item.CityName = city?.Name;
        if (city != null && !string.IsNullOrWhiteSpace(city.StateName)) item.StateName = city.StateName;
        item.DistanceKm = km;
        item.IsNear = NearbyRules.IsNear(km);
        item.LabelKey = NearbyRules.LabelKey(km, item.CityName, centreCityName);
    }
}
