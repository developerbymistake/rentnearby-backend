using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

// Loads feed rows by id. No phone, no lat/lng, no upi in the projection.
public static class StayFeedHydrator
{
    public static async Task<Dictionary<Guid, StayFeedItemDto>> LoadAsync(ApplicationDbContext db, IReadOnlyList<NearbyRow> rows)
    {
        var result = new Dictionary<Guid, StayFeedItemDto>();
        if (rows.Count == 0) return result;
        var ids = rows.Select(r => r.Id).Distinct().ToList();

        var items = await db.StayListings.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && !s.IsDeleted)
            .Select(s => new StayFeedItemDto
            {
                Id = s.Id,
                UserId = s.UserId,
                Name = s.Name,
                StayTypeId = s.StayTypeId,
                StayTypeName = s.StayType != null ? s.StayType.Name : null,
                ThumbnailUrl = s.Photos.OrderBy(p => p.PhotoOrder).Select(p => p.PhotoUrl).FirstOrDefault(),
                CityName = s.City != null ? s.City.Name : (s.District != null ? s.District.Name : null),
                DistrictName = s.District != null ? s.District.Name : null,
                StateName = s.District != null ? s.District.StateName : null,
                MinPricePerNight = s.MinPricePerNight,
                MealsChip = s.Rooms.Any(r => r.Meals == StayMeals.AllMeals) ? "All meals"
                    : s.Rooms.Any(r => r.Meals == StayMeals.Breakfast) ? "Breakfast"
                    : null,
                RoomNameChip = s.Rooms.OrderBy(r => r.PricePerNight).Select(r => r.RoomName).FirstOrDefault(),
                PriorityRank = s.PriorityRank,
                IsFeatured = s.IsFeatured,
                CreatedAt = s.CreatedAt,
            })
            .ToListAsync();

        var meters = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().DistanceMeters);
        foreach (var item in items)
        {
            var km = NearbyRules.StayKm(meters[item.Id]);
            item.DistanceKm = km;
            item.IsNear = NearbyRules.IsNear(km);
            item.LabelKey = NearbyRules.LabelKey(km, item.CityName, null);
            result[item.Id] = item;
        }
        return result;
    }
}
