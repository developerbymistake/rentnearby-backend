namespace RentNearBy.Core.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IUserRepository Users { get; }
    ISessionRepository Sessions { get; }
    IListingRepository Listings { get; }
    ICityRepository Cities { get; }
    IDistrictRepository Districts { get; }
    IRoomTypeRepository RoomTypes { get; }
    Task<int> SaveChangesAsync();
}
