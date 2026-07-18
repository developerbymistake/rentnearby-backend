using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class WalletRepository(ApplicationDbContext context) : IWalletRepository
{
    public async Task EnsureExistsAsync(Guid userId)
    {
        var now = DateTime.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"Wallets\" (\"UserId\", \"Balance\", \"CreatedAt\", \"UpdatedAt\") VALUES ({userId}, 0, {now}, {now}) ON CONFLICT (\"UserId\") DO NOTHING");
    }

    public async Task<int?> TryDebitAsync(Guid userId, int amount)
    {
        var affected = await context.Wallets
            .Where(w => w.UserId == userId && w.Balance >= amount)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Balance, w => w.Balance - amount)
                .SetProperty(w => w.UpdatedAt, DateTime.UtcNow));

        return affected == 0 ? null : await GetBalanceAsync(userId);
    }

    public async Task<int> CreditAsync(Guid userId, int amount)
    {
        await context.Wallets
            .Where(w => w.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Balance, w => w.Balance + amount)
                .SetProperty(w => w.UpdatedAt, DateTime.UtcNow));

        return await GetBalanceAsync(userId);
    }

    public async Task<int> GetBalanceAsync(Guid userId)
        => await context.Wallets
            .Where(w => w.UserId == userId)
            .Select(w => w.Balance)
            .FirstOrDefaultAsync();
}
