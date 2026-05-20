namespace RentNearBy.Core.Interfaces;

public interface IUnitOfWork : IDisposable
{
    IUserRepository Users { get; }
    ISessionRepository Sessions { get; }
    IListingRepository Listings { get; }
    IDistrictRepository Districts { get; }
    ICityRepository Cities { get; }
    IRoomTypeRepository RoomTypes { get; }
    IPlanRepository Plans { get; }
    IUserMembershipRepository UserMemberships { get; }
    IPaymentTransactionRepository PaymentTransactions { get; }
    IPaymentFeatureRepository PaymentFeature { get; }
    IPlotRepository Plots { get; }
    IPlotMembershipRepository PlotMemberships { get; }
    IPlotPaymentFeatureRepository PlotPaymentFeature { get; }
    IPlotPlanRepository PlotPlans { get; }
    Task<int> SaveChangesAsync();
}
