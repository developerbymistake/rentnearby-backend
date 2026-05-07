using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;
    private IUserRepository? _users;
    private ISessionRepository? _sessions;
    private IListingRepository? _listings;
    private ICityRepository? _cities;
    private IDistrictRepository? _districts;
    private IRoomTypeRepository? _roomTypes;

    public UnitOfWork(ApplicationDbContext context)
    {
        _context = context;
    }

    public IUserRepository Users => _users ??= new UserRepository(_context);
    public ISessionRepository Sessions => _sessions ??= new SessionRepository(_context);
    public IListingRepository Listings => _listings ??= new ListingRepository(_context);
    public ICityRepository Cities => _cities ??= new CityRepository(_context);
    public IDistrictRepository Districts => _districts ??= new DistrictRepository(_context);
    public IRoomTypeRepository RoomTypes => _roomTypes ??= new RoomTypeRepository(_context);

    public async Task<int> SaveChangesAsync()
        => await _context.SaveChangesAsync();

    public void Dispose()
        => _context.Dispose();
}
