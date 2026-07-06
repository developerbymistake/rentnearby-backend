using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class AdminDeviceTokenRepository(ApplicationDbContext context) : IAdminDeviceTokenRepository
{
    public async Task UpsertAsync(Guid adminId, string token)
    {
        var existing = await context.AdminDeviceTokens
            .FirstOrDefaultAsync(d => d.Token == token && d.AdminId == adminId);

        if (existing != null)
        {
            existing.IsValid = true;
            existing.UpdatedAt = DateTime.UtcNow;
            return;
        }

        // If same token belongs to a different admin (device reassigned), invalidate old record
        var staleOwner = await context.AdminDeviceTokens
            .FirstOrDefaultAsync(d => d.Token == token && d.AdminId != adminId);
        if (staleOwner != null)
            staleOwner.IsValid = false;

        await context.AdminDeviceTokens.AddAsync(new AdminDeviceToken
        {
            Id = Guid.NewGuid(),
            AdminId = adminId,
            Token = token,
            IsValid = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
    }

    public async Task<IEnumerable<AdminDeviceToken>> GetAllValidAsync()
        => await context.AdminDeviceTokens
            .Where(d => d.IsValid)
            .AsNoTracking()
            .ToListAsync();

    public async Task MarkInvalidAsync(string token)
    {
        var entity = await context.AdminDeviceTokens
            .FirstOrDefaultAsync(d => d.Token == token);
        if (entity != null)
        {
            entity.IsValid = false;
            entity.UpdatedAt = DateTime.UtcNow;
        }
    }

    public async Task DeleteByAdminIdAsync(Guid adminId)
        => await context.AdminDeviceTokens
            .Where(d => d.AdminId == adminId)
            .ExecuteDeleteAsync();
}
