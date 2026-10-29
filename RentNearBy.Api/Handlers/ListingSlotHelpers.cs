using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Utils;

namespace RentNearBy.Api.Handlers;

// Shared room/plot slot steps for the listing handlers.
internal static class ListingSlotHelpers
{
    internal sealed record CreateOutcome(bool Inserted, int? Limit, int? Used);

    // Creation under the owner lock: count (paused/pending/drafts count, Rejected do not) vs the plan limit,
    // then insert. A slug collision aborts the transaction, so each attempt is its own transaction.
    internal static async Task<CreateOutcome> InsertWithinLimitAsync(
        IUnitOfWork unitOfWork,
        ILiveSlotService slots,
        IPlanEntitlementService entitlements,
        Guid userId,
        string kind,
        string baseSlug,
        Action<string> setSlug,
        Action<PlanLimits> applyLimits)
    {
        int? limitHit = null, usedHit = null;
        var inserted = await SlugGenerator.GenerateUniqueSlugWithRetryAsync(
            baseSlug,
            setSlug,
            async () =>
            {
                await unitOfWork.BeginTransactionAsync();
                try
                {
                    await slots.LockOwnerAsync(userId);
                    var limits = await entitlements.GetLimitsAsync(userId, kind);
                    var used = await slots.CountForCreationAsync(userId, kind);
                    if (used >= limits.ListingLimit)
                    {
                        limitHit = limits.ListingLimit;
                        usedHit = used;
                        await unitOfWork.RollbackTransactionAsync();
                        return true;
                    }
                    applyLimits(limits);
                    await unitOfWork.SaveChangesAsync();
                    await unitOfWork.CommitTransactionAsync();
                    return true;
                }
                catch (DbUpdateException)
                {
                    await unitOfWork.RollbackTransactionAsync();
                    return false;
                }
                catch
                {
                    await unitOfWork.RollbackTransactionAsync();
                    throw;
                }
            });
        return limitHit != null ? new CreateOutcome(false, limitHit, usedHit) : new CreateOutcome(inserted, null, null);
    }
}
