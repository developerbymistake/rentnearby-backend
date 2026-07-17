using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Interfaces;

public interface ICoinTransactionRepository
{
    Task AddAsync(CoinTransaction transaction);

    // A single user's own ledger — offset-paginated, bounded by that user's own transaction count.
    Task<PagedResult<CoinTransaction>> GetPagedForUserAsync(Guid userId, int page, int pageSize, string? reason);

    // The admin-wide ledger, across every user. Deliberately keyset/cursor-paginated (lastCreatedAt +
    // lastId), not offset — this table has no natural per-row bound the way a single user's own
    // history does, and deep OFFSET scans degrade badly once it spans a large user base.
    Task<(IReadOnlyList<CoinTransactionWithUserResponse> Items, bool HasMore)> GetKeysetPagedWithUserAsync(
        int pageSize, DateTime? afterCreatedAt, Guid? afterId, Guid? userId, string? reason);
}
