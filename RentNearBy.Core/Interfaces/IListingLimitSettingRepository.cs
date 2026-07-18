using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IListingLimitSettingRepository : IRepository<ListingLimitSetting>
{
    Task<ListingLimitSetting?> GetByKindAsync(string kind);

    // Atomic UPDATE, not read-then-write — closes the lost-update race a naive load/modify/save
    // would have if two admins edited the same row at once.
    Task<int> UpdateMaxListingsAsync(string kind, int maxListings);
}
