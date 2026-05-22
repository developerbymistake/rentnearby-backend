using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;
    private IUserRepository? _users;
    private ISessionRepository? _sessions;
    private IAdminRepository? _admins;
    private IAdminSessionRepository? _adminSessions;
    private IListingRepository? _listings;
    private IDistrictRepository? _districts;
    private ICityRepository? _cities;
    private IRoomTypeRepository? _roomTypes;
    private IPlotTypeRepository? _plotTypes;
    private IPlanRepository? _plans;
    private IUserMembershipRepository? _userMemberships;
    private IPaymentTransactionRepository? _paymentTransactions;
    private IFeatureRepository? _features;
    private IPlotRepository? _plots;
    private IPlotMembershipRepository? _plotMemberships;
    private IPlotPlanRepository? _plotPlans;

    public UnitOfWork(ApplicationDbContext context)
    {
        _context = context;
    }

    public IUserRepository Users => _users ??= new UserRepository(_context);
    public ISessionRepository Sessions => _sessions ??= new SessionRepository(_context);
    public IAdminRepository Admins => _admins ??= new AdminRepository(_context);
    public IAdminSessionRepository AdminSessions => _adminSessions ??= new AdminSessionRepository(_context);
    public IListingRepository Listings => _listings ??= new ListingRepository(_context);
    public IDistrictRepository Districts => _districts ??= new DistrictRepository(_context);
    public ICityRepository Cities => _cities ??= new CityRepository(_context);
    public IRoomTypeRepository RoomTypes => _roomTypes ??= new RoomTypeRepository(_context);
    public IPlotTypeRepository PlotTypes => _plotTypes ??= new PlotTypeRepository(_context);
    public IPlanRepository Plans => _plans ??= new PlanRepository(_context);
    public IUserMembershipRepository UserMemberships => _userMemberships ??= new UserMembershipRepository(_context);
    public IPaymentTransactionRepository PaymentTransactions => _paymentTransactions ??= new PaymentTransactionRepository(_context);
    public IFeatureRepository Features => _features ??= new FeatureRepository(_context);
    public IPlotRepository Plots => _plots ??= new PlotRepository(_context);
    public IPlotMembershipRepository PlotMemberships => _plotMemberships ??= new PlotMembershipRepository(_context);
    public IPlotPlanRepository PlotPlans => _plotPlans ??= new PlotPlanRepository(_context);

    public async Task<int> SaveChangesAsync()
        => await _context.SaveChangesAsync();

    public void Dispose()
        => _context.Dispose();
}
