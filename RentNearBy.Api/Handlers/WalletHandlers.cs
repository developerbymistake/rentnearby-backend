using System.Security.Claims;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Consumer-facing wallet — a user's own balance and own ledger. Reads through the same
// ICoinWalletService/ICoinTransactionRepository the spend/credit engine and admin ledger use;
// this is purely a read-only view over that shared data, never a second source of truth.
public static class WalletHandlers
{
    public static async Task<IResult> GetBalance(ClaimsPrincipal principal, ICoinWalletService wallet)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        var balance = await wallet.GetBalanceAsync(userId);
        return OkResponse(new { balance });
    }

    public static async Task<IResult> GetMyTransactions(
        ClaimsPrincipal principal,
        IUnitOfWork unitOfWork,
        int page = 1,
        int pageSize = 20,
        string? reason = null)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId))
            return UnauthorizedResponse();

        pageSize = Math.Clamp(pageSize, 1, 50);
        page = Math.Max(1, page);

        var paged = await unitOfWork.CoinTransactions.GetPagedForUserAsync(userId, page, pageSize, reason);
        var items = paged.Items.Select(t => new CoinTransactionDto
        {
            Id = t.Id,
            Amount = t.Amount,
            Reason = t.Reason,
            ReferenceId = t.ReferenceId,
            BalanceAfter = t.BalanceAfter,
            Note = t.Note,
            CreatedAt = t.CreatedAt,
        }).ToList();

        return OkResponse(new { items, hasMore = paged.HasMore });
    }
}
