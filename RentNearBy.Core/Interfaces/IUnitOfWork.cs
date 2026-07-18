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
    ICoinPlanRepository CoinPlans { get; }
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
    IWalletRepository Wallets { get; }
    ICoinTransactionRepository CoinTransactions { get; }
    ICoinPackRepository CoinPacks { get; }
    IListingLimitSettingRepository ListingLimitSettings { get; }
    ICouponRepository Coupons { get; }
    ICouponRedemptionRepository CouponRedemptions { get; }
    ICoinPackPurchaseRepository CoinPackPurchases { get; }
    IServiceSectionRepository ServiceSections { get; }
    IServiceCategoryRepository ServiceCategories { get; }
    IServiceRepository Services { get; }
    IServicePackageRepository ServicePackages { get; }
    IInclusionRepository Inclusions { get; }
    IAgentRepository Agents { get; }
    IInquiryRepository Inquiries { get; }
    IInquiryStatusHistoryRepository InquiryStatusHistories { get; }
    Task<int> SaveChangesAsync();

    // Canonical transaction-control surface for handler/service-level code that needs multiple
    // writes (e.g. a coin spend + a listing activation) to commit or fail together. Wraps the same
    // underlying ApplicationDbContext's transaction, so ICoinWalletService's own ambient-transaction
    // detection sees it correctly regardless of which caller opened it.
    Task BeginTransactionAsync();
    Task CommitTransactionAsync();
    Task RollbackTransactionAsync();
}
