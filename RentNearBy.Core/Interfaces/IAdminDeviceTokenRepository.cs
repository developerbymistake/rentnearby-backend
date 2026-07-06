using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IAdminDeviceTokenRepository
{
    Task UpsertAsync(Guid adminId, string token);
    Task<IEnumerable<AdminDeviceToken>> GetAllValidAsync();
    Task MarkInvalidAsync(string token);
    Task DeleteByAdminIdAsync(Guid adminId);
}
