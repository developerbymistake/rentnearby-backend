namespace RentNearBy.Core.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IUserRepository Users { get; }
    ISessionRepository Sessions { get; }
    IAdminRepository Admins { get; }
    IAdminSessionRepository AdminSessions { get; }
    IRoomRoomListingRepository RoomListings { get; }
    IDistrictRepository Districts { get; }
    ICityRepository Cities { get; }
    IRoomTypeRepository RoomTypes { get; }
    IPlotTypeRepository PlotTypes { get; }
    IPlotListingRoomListingRepository PlotListings { get; }
    IStayListingRepository Stays { get; }
    IStayTypeRepository StayTypes { get; }
    IStayJobRepository StayJobs { get; }
    ITaxiListingRepository Taxis { get; }
    ITaxiVehicleTypeRepository TaxiVehicleTypes { get; }
    IDeviceTokenRepository DeviceTokens { get; }
    INotificationLogRepository NotificationLogs { get; }
    IDistrictBannerRepository DistrictBanners { get; }
    IReportReasonRepository ReportReasons { get; }
    IListingReportRepository ListingReports { get; }
    IAdminDeviceTokenRepository AdminDeviceTokens { get; }
    IConversationRepository Conversations { get; }
    IMessageRepository Messages { get; }
    IUserBlockRepository UserBlocks { get; }
    IQuestionTemplateRepository QuestionTemplates { get; }
    IAppFeatureFlagRepository AppFeatureFlags { get; }
    IAppTabRepository AppTabs { get; }
    IAppVersionConfigRepository AppVersionConfigs { get; }
    ICouponRepository Coupons { get; }
    ICouponRedemptionRepository CouponRedemptions { get; }
    IPlanRepository Plans { get; }
    IUserPlanRepository UserPlans { get; }
    IPlanPurchaseRepository PlanPurchases { get; }
    IPaymentEventRepository PaymentEvents { get; }
    INotificationRepository Notifications { get; }
    IRoleRepository Roles { get; }
    IRoleChangeRequestRepository RoleChangeRequests { get; }
    Task<int> SaveChangesAsync();

    // Canonical transaction-control surface for handler/service-level code that needs multiple
    // writes (e.g. a plan grant + its listing effects) to commit or fail together. Wraps the same
    // underlying ApplicationDbContext's transaction, so the plan services' ambient-transaction
    // detection sees it correctly regardless of which caller opened it.
    Task BeginTransactionAsync();
    Task CommitTransactionAsync();
    Task RollbackTransactionAsync();
}
