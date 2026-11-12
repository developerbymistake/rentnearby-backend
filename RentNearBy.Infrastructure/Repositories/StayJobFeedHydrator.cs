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

        var byId = loaded.ToDictionary(x => x.Item.Id);
        foreach (var row in rows)
        {
            if (!byId.TryGetValue(row.Id, out var x) || result.ContainsKey(row.Id)) continue;
            var city = x.Latitude is { } la && x.Longitude is { } lo
                ? (await resolver.ResolveAsync((double)la, (double)lo)).City
                : null;
            Fill(x.Item, row.DistanceMeters, city, centreCityName);
            result[row.Id] = x.Item;
        }
        return result;
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
