using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface IListingRepository : IRepository<Listing>
{
    Task<IEnumerable<NearbyListingResult>> GetNearbyAsync(double latitude, double longitude, double radiusKm, Guid cityId);
    Task<IEnumerable<Listing>> SearchAsync(Guid? cityId, Guid? roomTypeId, int? priceMin, int? priceMax);
    Task<IEnumerable<Listing>> GetByUserIdAsync(Guid userId);
    Task<Listing?> GetByIdWithPhotosAsync(Guid id);
}
