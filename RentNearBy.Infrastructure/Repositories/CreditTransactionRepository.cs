using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Extensions;

namespace RentNearBy.Infrastructure.Repositories;

public class CreditTransactionRepository(ApplicationDbContext context) : ICreditTransactionRepository
{
    public async Task AddAsync(CreditTransaction transaction)
        => await context.CreditTransactions.AddAsync(transaction);

    public async Task<PagedResult<CreditTransaction>> GetPagedForUserAsync(Guid userId, int page, int pageSize, string? reason)
    {
        var query = context.CreditTransactions.Where(t => t.UserId == userId);
        if (!string.IsNullOrWhiteSpace(reason))
            query = query.Where(t => t.Reason == reason);

        return await query.OrderByDescending(t => t.CreatedAt).ToPagedResultAsync(page, pageSize);
    }

    public async Task<(IReadOnlyList<CreditTransactionWithUserResponse> Items, bool HasMore)> GetKeysetPagedWithUserAsync(
        int pageSize, DateTime? afterCreatedAt, Guid? afterId, Guid? userId, string? reason)
    {
        var query = context.CreditTransactions.AsQueryable();
        if (userId.HasValue) query = query.Where(t => t.UserId == userId.Value);
        if (!string.IsNullOrWhiteSpace(reason)) query = query.Where(t => t.Reason == reason);
        if (afterCreatedAt.HasValue && afterId.HasValue)
            query = query.Where(t => t.CreatedAt < afterCreatedAt.Value
                || (t.CreatedAt == afterCreatedAt.Value && t.Id.CompareTo(afterId.Value) < 0));

        var rows = await query
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Take(pageSize + 1)
            .Select(t => new CreditTransactionWithUserResponse
            {
                Id = t.Id,
                UserId = t.UserId,
                UserPhoneNumber = t.User.PhoneNumber,
                UserName = t.User.Name,
                Amount = t.Amount,
                Reason = t.Reason,
                ReferenceId = t.ReferenceId,
                BalanceAfter = t.BalanceAfter,
                Note = t.Note,
                PerformedByUserId = t.PerformedByUserId,
                PerformedByUserPhoneNumber = t.PerformedByUser != null ? t.PerformedByUser.PhoneNumber : null,
                CreatedAt = t.CreatedAt,
            })
            .ToListAsync();

        var hasMore = rows.Count > pageSize;
        return (hasMore ? rows.Take(pageSize).ToList() : rows, hasMore);
    }
}
