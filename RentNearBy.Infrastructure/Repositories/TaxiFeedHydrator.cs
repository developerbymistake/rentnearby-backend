using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

// Loads the card fields of taxis found by distance. Coordinates are never read, only the grid-snapped distance is shown.
public static class TaxiFeedHydrator
{
    private sealed record Row(
        Guid Id, string? DriverName, string? VehicleTypeName, string? VehicleCategory, short? Seats, string? NumberPlate,
        Guid? CityId, string? CityName, string? DistrictName, string? StateName, int? MinFare, int RouteCount, DateTime CreatedAt,
        short PriorityRank, bool IsFeatured, int? LocalDayRate, int? LocalDayKm, int Received, int Answered);

    public static async Task<Dictionary<Guid, TaxiFeedItemDto>> LoadAsync(ApplicationDbContext db, IReadOnlyList<NearbyRow> rows)
    {
        var result = new Dictionary<Guid, TaxiFeedItemDto>();
        if (rows.Count == 0) return result;
        var ids = rows.Select(r => r.Id).Distinct().ToList();
        var loaded = await db.TaxiListings.AsNoTracking()
            .Where(t => ids.Contains(t.Id) && !t.IsDeleted)
            .Select(t => new Row(
                t.Id, t.User.Name, t.VehicleType != null ? t.VehicleType.Name : null,
                t.VehicleType != null ? t.VehicleType.Category : null, t.Seats, t.NumberPlate, t.CityId,
                t.City != null ? t.City.Name : null, t.District != null ? t.District.Name : null,
                t.District != null ? t.District.StateName : null, t.MinFare, t.Routes.Count, t.CreatedAt,
                t.PriorityRank, t.IsFeatured, t.LocalDayRate, t.LocalDayKm, t.TripLeadsReceived, t.TripLeadsAnsweredInApp))
            .ToListAsync();
        var byId = loaded.ToDictionary(r => r.Id);
        foreach (var row in rows)
        {
            if (!byId.TryGetValue(row.Id, out var r) || result.ContainsKey(row.Id)) continue;
            result[row.Id] = Map(r, row.DistanceMeters);
        }
        return result;
    }

    private static TaxiFeedItemDto Map(Row r, double meters)
    {
        var km = NearbyRules.TaxiKm(meters);
        var city = r.CityName ?? r.DistrictName;
        return new TaxiFeedItemDto
        {
            Id = r.Id, DriverName = r.DriverName, VehicleTypeName = r.VehicleTypeName, VehicleCategory = r.VehicleCategory,
            Seats = r.Seats, PlateDisplay = TaxiPlate.Format(r.NumberPlate), CityId = r.CityId, CityName = city,
            DistrictName = r.DistrictName, MinFare = r.MinFare, RouteCount = r.RouteCount,
            LocalDayRate = r.LocalDayRate, LocalDayKm = r.LocalDayKm,
            TripReplyLabel = TaxiTripReplyLabels.Of(r.Received, r.Answered),
            PriorityRank = r.PriorityRank, IsFeatured = r.IsFeatured, CreatedAt = r.CreatedAt,
            StateName = r.StateName, DistanceKm = km,
            LabelKey = NearbyRules.LabelKey(km, city, null), IsNear = NearbyRules.IsNear(km), ListedForRoute = false,
        };
    }
}
