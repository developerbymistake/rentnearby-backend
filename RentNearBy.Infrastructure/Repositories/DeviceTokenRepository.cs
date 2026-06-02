using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class DeviceTokenRepository(ApplicationDbContext context) : IDeviceTokenRepository
{
    public async Task UpsertAsync(Guid userId, string token)
    {
        var existing = await context.DeviceTokens
            .FirstOrDefaultAsync(d => d.Token == token && d.UserId == userId);

        if (existing != null)
        {
            existing.IsValid = true;
            existing.UpdatedAt = DateTime.UtcNow;
            return;
        }

        // If same token belongs to a different user (device reassigned), invalidate old record
        var staleOwner = await context.DeviceTokens
            .FirstOrDefaultAsync(d => d.Token == token && d.UserId != userId);
        if (staleOwner != null)
            staleOwner.IsValid = false;

        await context.DeviceTokens.AddAsync(new DeviceToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Token = token,
            IsValid = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
    }

    public async Task<IEnumerable<DeviceToken>> GetValidByUserIdAsync(Guid userId)
        => await context.DeviceTokens
            .Where(d => d.UserId == userId && d.IsValid)
            .AsNoTracking()
            .ToListAsync();

    public async Task MarkInvalidAsync(string token)
    {
        var entity = await context.DeviceTokens
            .FirstOrDefaultAsync(d => d.Token == token);
        if (entity != null)
        {
            entity.IsValid = false;
            entity.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task DeleteByUserIdAsync(Guid userId)
        => await context.DeviceTokens
            .Where(d => d.UserId == userId)
            .ExecuteDeleteAsync();

    public async Task<int> GetValidTokenUserCountAsync()
        => await context.DeviceTokens
            .Where(d => d.IsValid)
            .Select(d => d.UserId)
            .Distinct()
            .CountAsync();

    public async Task<IReadOnlyList<Guid>> GetValidTokenUserIdsPagedAsync(int offset, int limit)
        => await context.DeviceTokens
            .Where(d => d.IsValid)
            .Select(d => d.UserId)
            .Distinct()
            .OrderBy(id => id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
}
