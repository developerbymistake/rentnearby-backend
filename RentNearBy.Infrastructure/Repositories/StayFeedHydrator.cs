using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
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
        var today = IstTime.TodayIst(DateTime.UtcNow);
        var horizon = today.AddDays(OwnerLimits.StaysChipWithinDays);
        var rules = db.Set<StaySpecialPrice>();

        var loaded = await db.StayListings.AsNoTracking()
            .Where(s => ids.Contains(s.Id) && !s.IsDeleted)
            .Select(s => new
            {
                Item = new StayFeedItemDto
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
            },
                // T3: the chip of the live Lower rule running within the next 30 days, same statement.
                Chip = rules
                    .Where(r => r.StayListingId == s.Id && r.Direction == SpecialPriceDirections.Lower && !r.IsPaused && !r.PausedByPlan
                        && r.DateTo >= today && r.DateFrom <= horizon)
                    .OrderBy(r => r.DateFrom).ThenByDescending(r => r.Percent)
                    .Select(r => (int?)r.Percent)
                    .FirstOrDefault(),
            })
            .ToListAsync();
        var items = loaded.Select(x =>
        {
            x.Item.PriceChip = PriceChipDto.Of(x.Chip);
            x.Item.SetOffer(StayOfferFields.Undated(x.Item.MinPricePerNight, x.Chip));
            return x.Item;
        }).ToList();

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
