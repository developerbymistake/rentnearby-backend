using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IDeviceTokenRepository
{
    Task UpsertAsync(Guid userId, string token);
    Task<IEnumerable<DeviceToken>> GetValidByUserIdAsync(Guid userId);
    Task MarkInvalidAsync(string token);
    Task DeleteByUserIdAsync(Guid userId);
}
