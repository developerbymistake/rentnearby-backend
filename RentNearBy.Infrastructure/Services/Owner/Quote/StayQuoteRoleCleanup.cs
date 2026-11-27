using Microsoft.Extensions.Logging;

namespace RentNearBy.Infrastructure.Services.Owner.Quote;

// S11: when an account leaves the Stay Owner role the quotation master (row deleted inside the role purge,
// ListingPurgeService.QuoteProfileSql) and its images go; this deletes the images after the caller's commit, best effort.
// Counter and issue rows stay (numbers only, no personal data) until their own purge.
public sealed class StayQuoteRoleCleanup(IQuoteImageStore store, ILogger<StayQuoteRoleCleanup> logger)
{
    public async Task DeleteImagesBestEffortAsync(Guid userId)
    {
        try
        {
            await store.DeleteAllQuoteImagesAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Quote image cleanup failed after the role change of {UserId}", userId);
        }
    }
}
