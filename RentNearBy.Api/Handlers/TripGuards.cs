using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Shared server gates of the trip journey: every forward move calls these (the app can be bypassed, the server cannot).
public static class TripGuards
{
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromHours(24);

    // ---------- trip not completed ----------

    public static IResult? TripNotCompleted(Trip trip) => trip.Status == TripStatuses.Completed ? CompletedError() : null;

    // One query by trip id; a null trip id (trip-less row) is not blocked.
    public static async Task<IResult?> TripNotCompletedAsync(ApplicationDbContext db, Guid? tripId)
    {
        if (tripId is not { } id) return null;
        var status = await db.Trips.AsNoTracking().Where(t => t.Id == id).Select(t => t.Status).FirstOrDefaultAsync();
        return status == TripStatuses.Completed ? CompletedError() : null;
    }

    private static IResult CompletedError()
        => ConflictResponse("This trip is complete. Plan changes are off.", TripEnquiryErrorTypes.TripCompleted);

    // Row lock of the trip: parallel accepts and sends on one trip run one after the other. Call inside a transaction.
    public static async Task LockTripAsync(ApplicationDbContext db, Guid tripId)
        => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Trips\" WHERE \"Id\" = {tripId} FOR UPDATE");

    // ---------- listing approved ----------

    public static bool ListingLive(bool isDeleted, string status, string approvedStatus) => !isDeleted && status == approvedStatus;

    // 409 ListingNotAvailable unless the stay or taxi is still Approved and not deleted (host side forward actions).
    public static async Task<IResult?> ListingApprovedAsync(ApplicationDbContext db, string kind, Guid listingId)
    {
        var live = kind == TripEnquiryKinds.Taxi
            ? await db.TaxiListings.AsNoTracking().AnyAsync(t => t.Id == listingId && !t.IsDeleted && t.Status == TaxiStatuses.Approved)
            : await db.StayListings.AsNoTracking().AnyAsync(s => s.Id == listingId && !s.IsDeleted && s.Status == StayStatuses.Approved);
        return live ? null : ConflictResponse("This listing is not available any more.", TripEnquiryErrorTypes.ListingNotAvailable);
    }

    // ---------- step: in the plan, right kind, still open ----------

    // Null = the step key fits the plan, the kind and the day; else the reason (400 StepMismatch). day = the stay's first day.
    public static string? StepMismatch(TripPlan plan, string? stepKey, string kind, int? day = null)
    {
        if (!TripStepKeys.IsValid(stepKey) || !TripEasyRows.StepKeysOf(plan).Contains(stepKey!))
            return "This step is not in your plan.";
        switch (kind)
        {
            case TripShortlistKinds.Stay:
                if (!stepKey!.StartsWith("stay:", StringComparison.Ordinal)) return "This step is not for a stay.";
                if (day is { } d && stepKey != $"stay:{plan.LegIndexOf(d)}") return "These nights are in another city.";
                return null;
            case TripShortlistKinds.Taxi:
                return stepKey == TripRowKinds.Taxi ? null : "This step is not for a taxi.";
            case TripShortlistKinds.Car:
                return stepKey == TripRowKinds.GettingThere || stepKey == TripRowKinds.ComingBack
                    || stepKey!.StartsWith("between:", StringComparison.Ordinal) ? null : "This step is not for a car.";
            default:
                return "This step does not fit.";
        }
    }

    // True when the traveller settled the step himself (own or skipped), by a plan mark or a guide answer.
    public static bool StepClosed(TripPlan plan, string stepKey)
    {
        if (plan.Guide?.Choices is { } choices && choices.TryGetValue(stepKey, out var choice)
            && (choice == TripGuideChoices.Own || choice == TripGuideChoices.Skip))
            return true;
        var legs = plan.Legs ?? [];
        TripRowMark? mark = null;
        string? stepStatus = null;
        if (stepKey == TripRowKinds.Taxi) mark = plan.TaxiMark;
        else if (stepKey == TripRowKinds.GettingThere) stepStatus = plan.Steps?.GettingThere?.Status;
        else if (stepKey == TripRowKinds.ComingBack) stepStatus = plan.Steps?.ComingBack?.Status;
        else if (TryLeg(stepKey, "stay:", out var s) && s >= 1 && s <= legs.Count) mark = legs[s - 1]?.StayMark;
        else if (TryLeg(stepKey, "between:", out var b) && b >= 1 && b <= legs.Count) mark = legs[b - 1]?.TravelMark;
        return mark?.Status is TripRowMarkStatuses.Own or TripRowMarkStatuses.Skipped
            || stepStatus is TripStepStatuses.Own or TripStepStatuses.Skipped;
    }

    private static bool TryLeg(string key, string prefix, out int leg)
    {
        leg = 0;
        return key.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(key.AsSpan(prefix.Length), out leg);
    }

    // The step a send belongs to when the app named none: a stay's step is its city, a local taxi is "taxi".
    public static string DerivedStayStep(TripPlan plan, int day) => $"stay:{plan.LegIndexOf(day)}";

    // The step of a journey car by its plan role (segment = travel into city leg k); null for an unknown role.
    public static string? DerivedJourneyStep(string role, int legIndex) => role switch
    {
        TripStepRoles.GettingThere => TripRowKinds.GettingThere,
        TripStepRoles.ComingBack => TripRowKinds.ComingBack,
        TripStepRoles.Segment => $"between:{legIndex}",
        _ => null,
    };

    public static IResult StepMismatchError(string message) => BadRequestResponse(message, TripEnquiryErrorTypes.StepMismatch);

    public static IResult StepClosedError()
        => ConflictResponse("This step is already settled. Reopen it to ask hosts.", TripEnquiryErrorTypes.StepClosed);

    public static IResult StepFullError() => ConflictResponse(TripBulkRules.StepFullMessage, TripEnquiryErrorTypes.StepFull);

    // ---------- shortlist ownership ----------

    // The caller's own Active row for this trip, step and listing (the trip is already the caller's).
    public static async Task<bool> ActiveShortlistRowAsync(ApplicationDbContext db, Guid tripId, Guid callerId, string stepKey, Guid listingId)
        => await db.TripShortlistItems.AsNoTracking().AnyAsync(x => x.TripId == tripId && x.TravellerUserId == callerId
            && x.StepKey == stepKey && x.ListingId == listingId && x.State == TripShortlistStates.Active);

    // Of the requested listings, those that do not have an Active shortlist row of the caller (exempt = already open rows of the step).
    public static async Task<List<Guid>> NotShortlistedAsync(
        ApplicationDbContext db, Guid tripId, Guid callerId, string stepKey, IReadOnlyCollection<Guid> listingIds, IReadOnlySet<Guid> exempt)
    {
        var active = await db.TripShortlistItems.AsNoTracking()
            .Where(x => x.TripId == tripId && x.TravellerUserId == callerId && x.StepKey == stepKey
                && x.State == TripShortlistStates.Active && listingIds.Contains(x.ListingId))
            .Select(x => x.ListingId).ToListAsync();
        var have = active.ToHashSet();
        return listingIds.Where(id => !have.Contains(id) && !exempt.Contains(id)).ToList();
    }

    public static IResult NotShortlistedError()
        => ConflictResponse("Add these to your shortlist first.", TripEnquiryErrorTypes.NotShortlisted);

    // ---------- idempotent replay ----------

    // A replayed id counts only for a row of the same trip, step and kind that is still open and under 24 h old; any other row
    // that holds the id for a listing (the id is unique per traveller and listing) makes that listing "stale": it is sent as new
    // with no request id stored.
    public static (List<TripEnquiry> Replay, HashSet<Guid> StaleListings) SplitReplay(
        IEnumerable<TripEnquiry> rows, Guid tripId, string kind, string? stepKey, DateTime nowUtc)
    {
        var replay = new List<TripEnquiry>();
        var stale = new HashSet<Guid>();
        foreach (var row in rows)
        {
            var valid = row.TripId == tripId && row.Kind == kind && row.StepKey == stepKey
                && TripEnquiryStatuses.IsOpen(row.Status) && nowUtc - row.CreatedAt <= ReplayWindow;
            if (valid) replay.Add(row);
            else stale.Add(row.ListingId);
        }
        return (replay, stale);
    }

    // ---------- nights ----------

    // An open row stands for the request only when its nights contain every requested night.
    public static bool CoversNights(TripEnquiry open, DateOnly checkIn, DateOnly checkOut)
        => open.CheckIn is { } a && open.CheckOut is { } b && a <= checkIn && checkOut <= b;

    // ---------- step cap ----------

    // Open rows of the step plus the rows this send adds must stay within the shortlist cap (3).
    public static bool StepHasRoomFor(int openInStep, int newRows) => openInStep + newRows <= TripExecutionRules.MaxShortlistPerStep;
}
