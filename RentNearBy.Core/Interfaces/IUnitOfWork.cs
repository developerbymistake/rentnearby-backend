namespace RentNearBy.Core.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IUserRepository Users { get; }
    ISessionRepository Sessions { get; }
    IListingRepository Listings { get; }
    IDistrictRepository Districts { get; }
    ICityRepository Cities { get; }
    IRoomTypeRepository RoomTypes { get; }
    Task<int> SaveChangesAsync();
}
