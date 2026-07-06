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
    IRoomPlanRepository RoomPlans { get; }
    IRoomMembershipRepository RoomMemberships { get; }
    IPaymentTransactionRepository PaymentTransactions { get; }
    IFeatureRepository Features { get; }
    IPlotListingRoomListingRepository PlotListings { get; }
    IPlotMembershipRepository PlotMemberships { get; }
    IPlotPlanRepository PlotPlans { get; }
    IDeviceTokenRepository DeviceTokens { get; }
    INotificationLogRepository NotificationLogs { get; }
    IDistrictBannerRepository DistrictBanners { get; }
    IReportReasonRepository ReportReasons { get; }
    IListingReportRepository ListingReports { get; }
    IAdminDeviceTokenRepository AdminDeviceTokens { get; }
    Task<int> SaveChangesAsync();
}
