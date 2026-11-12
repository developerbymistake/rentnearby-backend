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
    private IDistrictRepository? _districts;
    private ICityRepository? _cities;
    private IStayListingRepository? _stays;
    private IStayTypeRepository? _stayTypes;
    private IStayRegistrationTypeRepository? _stayRegistrationTypes;
    private IStayJobRepository? _stayJobs;
    private ITaxiListingRepository? _taxis;
    private ITaxiVehicleTypeRepository? _taxiVehicleTypes;
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
    private IAppFeatureFlagRepository? _appFeatureFlags;
    private IAppTabRepository? _appTabs;
    private IAppVersionConfigRepository? _appVersionConfigs;
    private ICouponRepository? _coupons;
    private ICouponRedemptionRepository? _couponRedemptions;
    private IPlanRepository? _plans;
    private IUserPlanRepository? _userPlans;
    private IPlanPurchaseRepository? _planPurchases;
    private IPaymentEventRepository? _paymentEvents;
    private IPlaySubscriptionRepository? _playSubscriptions;
    private INotificationRepository? _notifications;
    private IRoleRepository? _roles;
    private IRoleChangeRequestRepository? _roleChangeRequests;
    private ITripRepository? _trips;
    private ITripEnquiryRepository? _tripEnquiries;
    private ITripOfferRepository? _tripOffers;
    private ITripPushLogRepository? _tripPushLogs;
    private ITripShortlistRepository? _tripShortlist;

    public UnitOfWork(ApplicationDbContext context)
    {
        _context = context;
    }

    public IUserRepository Users => _users ??= new UserRepository(_context);
    public ISessionRepository Sessions => _sessions ??= new SessionRepository(_context);
    public IAdminRepository Admins => _admins ??= new AdminRepository(_context);
    public IAdminSessionRepository AdminSessions => _adminSessions ??= new AdminSessionRepository(_context);
    public IDistrictRepository Districts => _districts ??= new DistrictRepository(_context);
    public ICityRepository Cities => _cities ??= new CityRepository(_context);
    public IStayListingRepository Stays => _stays ??= new StayListingRepository(_context);
    public IStayTypeRepository StayTypes => _stayTypes ??= new StayTypeRepository(_context);
    public IStayRegistrationTypeRepository StayRegistrationTypes => _stayRegistrationTypes ??= new StayRegistrationTypeRepository(_context);
    public IStayJobRepository StayJobs => _stayJobs ??= new StayJobRepository(_context);
    public ITaxiListingRepository Taxis => _taxis ??= new TaxiListingRepository(_context);
    public ITaxiVehicleTypeRepository TaxiVehicleTypes => _taxiVehicleTypes ??= new TaxiVehicleTypeRepository(_context);
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
    public IAppFeatureFlagRepository AppFeatureFlags => _appFeatureFlags ??= new AppFeatureFlagRepository(_context);
    public IAppTabRepository AppTabs => _appTabs ??= new AppTabRepository(_context);
    public IAppVersionConfigRepository AppVersionConfigs => _appVersionConfigs ??= new AppVersionConfigRepository(_context);
    public ICouponRepository Coupons => _coupons ??= new CouponRepository(_context);
    public ICouponRedemptionRepository CouponRedemptions => _couponRedemptions ??= new CouponRedemptionRepository(_context);
    public IPlanRepository Plans => _plans ??= new PlanRepository(_context);
    public IUserPlanRepository UserPlans => _userPlans ??= new UserPlanRepository(_context);
    public IPlanPurchaseRepository PlanPurchases => _planPurchases ??= new PlanPurchaseRepository(_context);
    public IPaymentEventRepository PaymentEvents => _paymentEvents ??= new PaymentEventRepository(_context);
    public IPlaySubscriptionRepository PlaySubscriptions => _playSubscriptions ??= new PlaySubscriptionRepository(_context);
    public INotificationRepository Notifications => _notifications ??= new NotificationRepository(_context);
    public IRoleRepository Roles => _roles ??= new RoleRepository(_context);
    public IRoleChangeRequestRepository RoleChangeRequests => _roleChangeRequests ??= new RoleChangeRequestRepository(_context);
    public ITripRepository Trips => _trips ??= new TripRepository(_context);
    public ITripEnquiryRepository TripEnquiries => _tripEnquiries ??= new TripEnquiryRepository(_context);
    public ITripOfferRepository TripOffers => _tripOffers ??= new TripOfferRepository(_context);
    public ITripPushLogRepository TripPushLogs => _tripPushLogs ??= new TripPushLogRepository(_context);
    public ITripShortlistRepository TripShortlist => _tripShortlist ??= new TripShortlistRepository(_context);

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
