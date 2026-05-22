namespace RentNearBy.Core.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IUserRepository Users { get; }
    ISessionRepository Sessions { get; }
    IAdminRepository Admins { get; }
    IAdminSessionRepository AdminSessions { get; }
    IListingRepository Listings { get; }
    IDistrictRepository Districts { get; }
    ICityRepository Cities { get; }
    IRoomTypeRepository RoomTypes { get; }
    IPlotTypeRepository PlotTypes { get; }
    IPlanRepository Plans { get; }
    IUserMembershipRepository UserMemberships { get; }
    IPaymentTransactionRepository PaymentTransactions { get; }
    IFeatureRepository Features { get; }
    IPlotRepository Plots { get; }
    IPlotMembershipRepository PlotMemberships { get; }
    IPlotPlanRepository PlotPlans { get; }
    Task<int> SaveChangesAsync();
}
