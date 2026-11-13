using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface IDeviceTokenRepository
{
    Task UpsertAsync(Guid userId, string token);
    Task<IEnumerable<DeviceToken>> GetValidByUserIdAsync(Guid userId);
    Task MarkInvalidAsync(string token);
    Task DeleteByUserIdAsync(Guid userId);
    Task DeleteForUserAsync(Guid userId, string token);
    Task<int> GetValidTokenUserCountAsync();
    Task<IReadOnlyList<Guid>> GetValidTokenUserIdsPagedAsync(int offset, int limit);
    // Keyset by DeviceToken.Id so MarkInvalid between pages cannot shift the window.
    Task<IReadOnlyList<(Guid Id, string Token)>> GetValidTokensByRoleAfterAsync(Guid roleId, Guid? afterId, int limit);
    Task<IReadOnlyList<(Guid UserId, string Token)>> GetValidByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
    Task<int> MarkInvalidManyAsync(IReadOnlyCollection<string> tokens, CancellationToken ct = default);
}
