using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPlotPlanRepository
{
    Task<PlotPlan?> GetByPlanTypeAsync(string planType);
    Task<IEnumerable<PlotPlan>> GetAllAsync();
}
