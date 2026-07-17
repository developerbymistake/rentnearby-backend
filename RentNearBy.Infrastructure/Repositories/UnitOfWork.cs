using Microsoft.EntityFrameworkCore.Storage;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _context;
    private IDbContextTransaction? _currentTransaction;
    private IUserRepository? _users;
    private ISessionRepository? _sessions;
    private IAdminRepository? _admins;
    private IAdminSessionRepository? _adminSessions;
    private IRoomRoomListingRepository? _listings;
    private IDistrictRepository? _districts;
    private ICityRepository? _cities;
    private IRoomTypeRepository? _roomTypes;
    private IPlotTypeRepository? _plotTypes;
    private IRoomPlanRepository? _roomPlans;
    private IPlotListingRoomListingRepository? _plots;
    private IPlotPlanRepository? _plotPlans;
    private IDeviceTokenRepository? _deviceTokens;
    private INotificationLogRepository? _notificationLogs;
    private IDistrictBannerRepository? _districtBanners;
    private IReportReasonRepository? _reportReasons;
    private IListingReportRepository? _listingReports;
    private IAdminDeviceTokenRepository? _adminDeviceTokens;
    private IConversationRepository? _conversations;
    private IMessageRepository? _messages;
    private IUserBlockRepository? _userBlocks;
    private IQuestionTemplateRepository? _questionTemplates;
    private IWalletRepository? _wallets;
    private ICoinTransactionRepository? _coinTransactions;
    private ICoinPackRepository? _coinPacks;
    private IListingLimitSettingRepository? _listingLimitSettings;
    private ICouponRepository? _coupons;
    private ICouponRedemptionRepository? _couponRedemptions;
    private ICoinPackPurchaseRepository? _coinPackPurchases;

    public UnitOfWork(ApplicationDbContext context)
    {
        _context = context;
    }

    public IUserRepository Users => _users ??= new UserRepository(_context);
    public ISessionRepository Sessions => _sessions ??= new SessionRepository(_context);
    public IAdminRepository Admins => _admins ??= new AdminRepository(_context);
    public IAdminSessionRepository AdminSessions => _adminSessions ??= new AdminSessionRepository(_context);
    public IRoomRoomListingRepository RoomListings => _listings ??= new RoomListingRepository(_context);
    public IDistrictRepository Districts => _districts ??= new DistrictRepository(_context);
    public ICityRepository Cities => _cities ??= new CityRepository(_context);
    public IRoomTypeRepository RoomTypes => _roomTypes ??= new RoomTypeRepository(_context);
    public IPlotTypeRepository PlotTypes => _plotTypes ??= new PlotTypeRepository(_context);
    public IRoomPlanRepository RoomPlans => _roomPlans ??= new RoomPlanRepository(_context);
    public IPlotListingRoomListingRepository PlotListings => _plots ??= new PlotListingRepository(_context);
    public IPlotPlanRepository PlotPlans => _plotPlans ??= new PlotPlanRepository(_context);
    public IDeviceTokenRepository DeviceTokens => _deviceTokens ??= new DeviceTokenRepository(_context);
    public INotificationLogRepository NotificationLogs => _notificationLogs ??= new NotificationLogRepository(_context);
    public IDistrictBannerRepository DistrictBanners => _districtBanners ??= new DistrictBannerRepository(_context);
    public IReportReasonRepository ReportReasons => _reportReasons ??= new ReportReasonRepository(_context);
    public IListingReportRepository ListingReports => _listingReports ??= new ListingReportRepository(_context);
    public IAdminDeviceTokenRepository AdminDeviceTokens => _adminDeviceTokens ??= new AdminDeviceTokenRepository(_context);
    public IConversationRepository Conversations => _conversations ??= new ConversationRepository(_context);
    public IMessageRepository Messages => _messages ??= new MessageRepository(_context);
    public IUserBlockRepository UserBlocks => _userBlocks ??= new UserBlockRepository(_context);
    public IQuestionTemplateRepository QuestionTemplates => _questionTemplates ??= new QuestionTemplateRepository(_context);
    public IWalletRepository Wallets => _wallets ??= new WalletRepository(_context);
    public ICoinTransactionRepository CoinTransactions => _coinTransactions ??= new CoinTransactionRepository(_context);
    public ICoinPackRepository CoinPacks => _coinPacks ??= new CoinPackRepository(_context);
    public IListingLimitSettingRepository ListingLimitSettings => _listingLimitSettings ??= new ListingLimitSettingRepository(_context);
    public ICouponRepository Coupons => _coupons ??= new CouponRepository(_context);
    public ICouponRedemptionRepository CouponRedemptions => _couponRedemptions ??= new CouponRedemptionRepository(_context);
    public ICoinPackPurchaseRepository CoinPackPurchases => _coinPackPurchases ??= new CoinPackPurchaseRepository(_context);

    public async Task<int> SaveChangesAsync()
        => await _context.SaveChangesAsync();

    public async Task BeginTransactionAsync()
        => _currentTransaction = await _context.Database.BeginTransactionAsync();

    public async Task CommitTransactionAsync()
    {
        if (_currentTransaction == null) return;
        await _currentTransaction.CommitAsync();
        await _currentTransaction.DisposeAsync();
        _currentTransaction = null;
    }

    public async Task RollbackTransactionAsync()
    {
        if (_currentTransaction == null) return;
        await _currentTransaction.RollbackAsync();
        await _currentTransaction.DisposeAsync();
        _currentTransaction = null;
    }

    public void Dispose()
        => _context.Dispose();
}
