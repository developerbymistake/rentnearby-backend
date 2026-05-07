using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class ListingRepository(ApplicationDbContext context) : Repository<Listing>(context), IListingRepository
{
    public async Task<IEnumerable<NearbyListingResult>> GetNearbyAsync(double latitude, double longitude, double radiusKm, Guid districtId)
    {
        double latDelta = radiusKm / 111.0;
        double lngDelta = radiusKm / (111.0 * Math.Cos(latitude * Math.PI / 180.0));

        var candidates = await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l =>
                l.IsActive &&
                l.DistrictId == districtId &&
                (double)l.Latitude >= latitude - latDelta && (double)l.Latitude <= latitude + latDelta &&
                (double)l.Longitude >= longitude - lngDelta && (double)l.Longitude <= longitude + lngDelta)
            .ToListAsync();

        return candidates
            .Select(l => new NearbyListingResult(l, Haversine(latitude, longitude, (double)l.Latitude, (double)l.Longitude)))
            .Where(r => r.DistanceKm <= radiusKm)
            .OrderBy(r => r.DistanceKm);
    }

    public async Task<IEnumerable<Listing>> SearchAsync(Guid? districtId, Guid? roomTypeId, int? priceMin, int? priceMax)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder).Take(1))
            .Where(l =>
                l.IsActive &&
                (districtId == null || l.DistrictId == districtId) &&
                (roomTypeId == null || l.RoomTypeId == roomTypeId) &&
                (priceMin == null || l.PriceMonthly >= priceMin) &&
                (priceMax == null || l.PriceMonthly <= priceMax))
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

    public async Task<IEnumerable<Listing>> GetByUserIdAsync(Guid userId)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder))
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();

    public async Task<Listing?> GetByIdWithPhotosAsync(Guid id)
        => await _dbSet
            .AsNoTracking()
            .Include(l => l.RoomType)
            .Include(l => l.District)
            .Include(l => l.City)
            .Include(l => l.User)
            .Include(l => l.Photos.OrderBy(p => p.PhotoOrder))
            .FirstOrDefaultAsync(l => l.Id == id);

    private static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLng = (lng2 - lng1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
