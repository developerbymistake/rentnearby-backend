using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Repositories;

public class TripShortlistRepository(ApplicationDbContext context) : ITripShortlistRepository
{
    private const int NoReplyDays = 30;

    public async Task<IReadOnlyList<TripShortlistItem>> GetForTripAsync(Guid tripId, DateTime nowUtc)
    {
        var since = nowUtc.AddDays(-NoReplyDays);
        return await context.TripShortlistItems.AsNoTracking()
            .Where(x => x.TripId == tripId
                && (x.State == TripShortlistStates.Active
                    || (x.State == TripShortlistStates.Closed && x.ClosedReason == TripShortlistClosedReasons.NoReply && x.ClosedAt >= since)))
            // A listing that is deleted or off never shows (the traveller could not ask it any more).
            .Where(x => (x.Kind == TripShortlistKinds.Stay
                    && context.StayListings.Any(s => s.Id == x.ListingId && !s.IsDeleted && s.Status == StayStatuses.Approved && s.AcceptingGuests))
                || (x.Kind == TripShortlistKinds.Taxi
                    && context.TaxiListings.Any(t => t.Id == x.ListingId && !t.IsDeleted && t.Status == TaxiStatuses.Approved
                        && t.IsAvailable && t.LocalDayRate != null))
                || (x.Kind == TripShortlistKinds.Car
                    && context.TaxiListings.Any(t => t.Id == x.ListingId && !t.IsDeleted && t.Status == TaxiStatuses.Approved && t.IsAvailable)))
            .OrderBy(x => x.AddedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<TripShortlistItem>> GetActiveStepAsync(Guid tripId, string stepKey)
        => await context.TripShortlistItems.AsNoTracking()
            .Where(x => x.TripId == tripId && x.StepKey == stepKey && x.State == TripShortlistStates.Active)
            .OrderBy(x => x.AddedAt)
            .ToListAsync();

    public async Task<TripShortlistItem?> FindActiveAsync(Guid tripId, string stepKey, Guid listingId)
        => await context.TripShortlistItems
            .FirstOrDefaultAsync(x => x.TripId == tripId && x.StepKey == stepKey && x.ListingId == listingId
                && x.State == TripShortlistStates.Active);

    public async Task<TripShortlistItem?> FindNoReplyAsync(Guid tripId, string stepKey, Guid listingId)
        => await context.TripShortlistItems
            .Where(x => x.TripId == tripId && x.StepKey == stepKey && x.ListingId == listingId
                && x.State == TripShortlistStates.Closed && x.ClosedReason == TripShortlistClosedReasons.NoReply)
            .OrderByDescending(x => x.ClosedAt)
            .FirstOrDefaultAsync();

    public async Task<int> CountActiveAsync(Guid tripId, string stepKey)
        => await context.TripShortlistItems
            .CountAsync(x => x.TripId == tripId && x.StepKey == stepKey && x.State == TripShortlistStates.Active);

    public void Add(TripShortlistItem item) => context.TripShortlistItems.Add(item);

    public async Task<int> CloseOneAsync(Guid tripId, string stepKey, Guid listingId, string reason, DateTime nowUtc)
        => await context.TripShortlistItems
            .Where(x => x.TripId == tripId && x.StepKey == stepKey && x.ListingId == listingId && x.State == TripShortlistStates.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, TripShortlistStates.Closed)
                .SetProperty(x => x.ClosedReason, reason)
                .SetProperty(x => x.ClosedAt, nowUtc));

    // Active rows close with the reason; a NoReply row still waiting for its final push is settled too (no push for a step the
    // traveller closed).
    public async Task<int> CloseStepAsync(Guid tripId, string stepKey, string reason, DateTime nowUtc)
    {
        var closed = await context.TripShortlistItems
            .Where(x => x.TripId == tripId && x.StepKey == stepKey && x.State == TripShortlistStates.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, TripShortlistStates.Closed)
                .SetProperty(x => x.ClosedReason, reason)
                .SetProperty(x => x.ClosedAt, nowUtc));
        await context.TripShortlistItems
            .Where(x => x.TripId == tripId && x.StepKey == stepKey && x.State == TripShortlistStates.Closed
                && x.ClosedReason == TripShortlistClosedReasons.NoReply && x.FinalPushedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FinalPushedAt, nowUtc));
        return closed;
    }

    public async Task<int> MarkSentAsync(Guid tripId, string? stepKey, IReadOnlyCollection<Guid> listingIds, DateTime nowUtc)
        => await context.TripShortlistItems
            .Where(x => x.TripId == tripId && (stepKey == null || x.StepKey == stepKey)
                && listingIds.Contains(x.ListingId) && x.State == TripShortlistStates.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, TripShortlistStates.Sent)
                .SetProperty(x => x.ClosedReason, TripShortlistClosedReasons.Sent)
                .SetProperty(x => x.ClosedAt, nowUtc));

    public async Task<int> MarkBookedAsync(Guid tripId, string stepKey, Guid? listingId, DateTime nowUtc)
        => await context.TripShortlistItems
            .Where(x => x.TripId == tripId && x.StepKey == stepKey
                && (listingId == null || x.ListingId == listingId)
                && (x.State == TripShortlistStates.Active || x.State == TripShortlistStates.Sent
                    || (x.State == TripShortlistStates.Closed && x.ClosedReason == TripShortlistClosedReasons.NoReply && x.FinalPushedAt == null)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, TripShortlistStates.Booked)
                .SetProperty(x => x.ClosedReason, TripShortlistClosedReasons.Booked)
                .SetProperty(x => x.ClosedAt, nowUtc));
}
