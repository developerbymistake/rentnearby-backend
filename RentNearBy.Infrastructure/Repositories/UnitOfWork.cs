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
    private IRoomRoomListingRepository? _listings;
    private IDistrictRepository? _districts;
    private ICityRepository? _cities;
    private IRoomTypeRepository? _roomTypes;
    private IPlotTypeRepository? _plotTypes;
    private IRoomPlanRepository? _roomPlans;
    private IRoomMembershipRepository? _userMemberships;
    private IPaymentTransactionRepository? _paymentTransactions;
    private IFeatureRepository? _features;
    private IPlotListingRoomListingRepository? _plots;
    private IPlotMembershipRepository? _plotMemberships;
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
    public IRoomMembershipRepository RoomMemberships => _userMemberships ??= new RoomMembershipRepository(_context);
    public IPaymentTransactionRepository PaymentTransactions => _paymentTransactions ??= new PaymentTransactionRepository(_context);
    public IFeatureRepository Features => _features ??= new FeatureRepository(_context);
    public IPlotListingRoomListingRepository PlotListings => _plots ??= new PlotListingRepository(_context);
    public IPlotMembershipRepository PlotMemberships => _plotMemberships ??= new PlotMembershipRepository(_context);
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

    public async Task<int> SaveChangesAsync()
        => await _context.SaveChangesAsync();

    public void Dispose()
        => _context.Dispose();
}
