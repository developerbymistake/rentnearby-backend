using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IPlanRepository
{
    Task<Plan?> GetByPlanTypeAsync(string planType);
    Task<IEnumerable<Plan>> GetAllAsync();
    Task AddAsync(Plan plan);
    Task UpdateAsync(Plan plan);
    Task DeleteAsync(Plan plan);
}
