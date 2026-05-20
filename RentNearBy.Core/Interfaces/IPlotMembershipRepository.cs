using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPlotMembershipRepository
{
    Task AddAsync(PlotMembership membership);
    Task<PlotMembership?> GetActiveByUserIdAsync(Guid userId);
    Task<IEnumerable<PlotMembership>> GetExpiredAsync(DateTime beforeDate);
}
