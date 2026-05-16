using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;
    private IUserRepository? _users;
    private ISessionRepository? _sessions;
    private IListingRepository? _listings;
    private IDistrictRepository? _districts;
    private ICityRepository? _cities;
    private IRoomTypeRepository? _roomTypes;
    private IUserMembershipRepository? _userMemberships;
    private IPaymentTransactionRepository? _paymentTransactions;
    private IPaymentFeatureRepository? _paymentFeature;

    public UnitOfWork(ApplicationDbContext context)
    {
        _context = context;
    }

    public IUserRepository Users => _users ??= new UserRepository(_context);
    public ISessionRepository Sessions => _sessions ??= new SessionRepository(_context);
    public IListingRepository Listings => _listings ??= new ListingRepository(_context);
    public IDistrictRepository Districts => _districts ??= new DistrictRepository(_context);
    public ICityRepository Cities => _cities ??= new CityRepository(_context);
    public IRoomTypeRepository RoomTypes => _roomTypes ??= new RoomTypeRepository(_context);
    public IUserMembershipRepository UserMemberships => _userMemberships ??= new UserMembershipRepository(_context);
    public IPaymentTransactionRepository PaymentTransactions => _paymentTransactions ??= new PaymentTransactionRepository(_context);
    public IPaymentFeatureRepository PaymentFeature => _paymentFeature ??= new PaymentFeatureRepository(_context);

    public async Task<int> SaveChangesAsync()
        => await _context.SaveChangesAsync();

    public void Dispose()
        => _context.Dispose();
}
