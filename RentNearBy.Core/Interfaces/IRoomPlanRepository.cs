using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IRoomPlanRepository
{
    Task<RoomPlan?> GetByPlanTypeAsync(string planType);
    Task<IEnumerable<RoomPlan>> GetAllAsync();
    Task AddAsync(RoomPlan plan);
    Task UpdateAsync(RoomPlan plan);
    Task DeleteAsync(RoomPlan plan);
}
