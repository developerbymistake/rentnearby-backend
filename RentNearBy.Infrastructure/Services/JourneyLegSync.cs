using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// Keeps the saved journey leg whose current lead is e in step with e, inside the caller's transaction.
// Trip-less journey leads and segment rows (journey car between two cities, Day set) both count; a trip's local lead never does.
public static class JourneyLegSync
{
    public static async Task<int> SyncAsync(ApplicationDbContext db, TripEnquiry e, DateTime now)
    {
        if (e.Kind != TripEnquiryKinds.Taxi || (e.TripId != null && !TripSegmentRows.IsSegment(e))) return 0;
        if (ReadLeg(e.ContextJson) is not { } leg) return 0;
        var (journeyId, legIndex) = leg;

        var state = JourneyLegState.Of(e);
        return await db.Set<SavedJourneyLeg>()
            .Where(l => l.JourneyId == journeyId && l.LegIndex == legIndex && l.LastEnquiryId == e.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.LastKnownStatus, state.Status)
                .SetProperty(l => l.LastQuotedFare, state.QuotedFare)
                .SetProperty(l => l.LastKnownAt, (DateTime?)state.KnownAt)
                .SetProperty(l => l.UpdatedAt, now));
    }

    private static (Guid JourneyId, short LegIndex)? ReadLeg(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson)) return null;
        try
        {
            var context = System.Text.Json.JsonSerializer.Deserialize<TripEnquiryContext>(contextJson, TripJson.Options);
            return context is { JourneyId: { } id, LegIndex: { } index } && index is >= short.MinValue and <= short.MaxValue
                ? (id, (short)index)
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
