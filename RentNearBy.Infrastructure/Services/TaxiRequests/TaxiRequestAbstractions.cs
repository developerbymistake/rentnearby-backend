using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.TaxiRequests;

// Everything here runs inside the caller's transaction and never calls SaveChanges itself unless a method says so.

public sealed record RingPush(Guid OfferId, Guid RequestId, Guid DriverUserId, DateTime ExpiresAtUtc);

public sealed record RingEndPush(Guid OfferId, Guid RequestId, Guid DriverUserId, string Reason, string? ExceptToken = null);

// One value per row of the contract table 10.3.
public enum TaxiNoticeKind
{
    DriverAccepted,
    SelectionLapsed,
    Booked,
    RequestEnded,
    DriverWithdrew,
    RateDriver,
    TripEnded,
    Selected,
    NotSelected,
    RequestClosed,
    BookingCancelled,
    RingsMissed,
    ChooseDriver,
}

// PersonName is the other side's name for the copy that needs one ({driver}, {name}); Count is the number the copy shows
// (ChooseDriver: live accepted drivers); Reason is the offer end reason of a SelectionLapsed. Callers never pass copy.
public sealed record TaxiNotice(TaxiNoticeKind Kind, Guid UserId, Guid TripId, Guid RequestId, Guid? OfferId = null, string? PersonName = null, int Count = 0,
    string? Reason = null);

public static class TaxiNoticeTypes
{
    public static string TypeOf(TaxiNoticeKind kind) => kind switch
    {
        TaxiNoticeKind.DriverAccepted => NotificationTypes.TaxiDriverAccepted,
        TaxiNoticeKind.SelectionLapsed => NotificationTypes.TaxiSelectionLapsed,
        TaxiNoticeKind.Booked => NotificationTypes.TaxiBooked,
        TaxiNoticeKind.RequestEnded => NotificationTypes.TaxiRequestEnded,
        TaxiNoticeKind.DriverWithdrew => NotificationTypes.TaxiDriverWithdrew,
        TaxiNoticeKind.RateDriver => NotificationTypes.TaxiRateDriver,
        TaxiNoticeKind.TripEnded => NotificationTypes.TaxiTripEnded,
        TaxiNoticeKind.Selected => NotificationTypes.TaxiSelected,
        TaxiNoticeKind.NotSelected => NotificationTypes.TaxiNotSelected,
        TaxiNoticeKind.RequestClosed => NotificationTypes.TaxiRequestClosed,
        TaxiNoticeKind.BookingCancelled => NotificationTypes.TaxiBookingCancelled,
        TaxiNoticeKind.RingsMissed => NotificationTypes.TaxiRingsMissed,
        TaxiNoticeKind.ChooseDriver => NotificationTypes.TaxiChooseDriver,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown notice kind"),
    };
}

// Ring and ring end are outbox rows only; a notice is an inbox row plus an outbox row. Only the writer knows the words.
public interface ITaxiPushWriter
{
    Task RingAsync(ApplicationDbContext db, RingPush ring, CancellationToken ct = default);
    Task RingEndAsync(ApplicationDbContext db, RingEndPush ringEnd, CancellationToken ct = default);
    Task NotifyAsync(ApplicationDbContext db, TaxiNotice notice, CancellationToken ct = default);
}

public sealed record DueActionItem(Guid Id, string Kind, Guid SubjectId, int RoundNo, Guid? RequestId, DateTime DueAtUtc, short Attempts);

public enum DueOutcome
{
    Done,
    Skipped,
}

// DueAt is always computed by the database clock; delay forms add to now(), AddAt takes an absolute instant.
public interface IDueActionScheduler
{
    Task AddAsync(ApplicationDbContext db, string kind, Guid subjectId, Guid? requestId, int roundNo, TimeSpan delay, CancellationToken ct = default);
    Task AddAtAsync(ApplicationDbContext db, string kind, Guid subjectId, Guid? requestId, int roundNo, DateTime dueAtUtc, CancellationToken ct = default);
    Task CancelAsync(ApplicationDbContext db, string kind, Guid subjectId, CancellationToken ct = default);
    Task CancelForRequestAsync(ApplicationDbContext db, Guid requestId, IReadOnlyCollection<string> kinds, CancellationToken ct = default);
}

// One handler per due action kind; it re-checks state with a conditional update and reports Done or Skipped.
public interface IDueActionHandler
{
    string Kind { get; }
    Task<DueOutcome> HandleAsync(ApplicationDbContext db, DueActionItem item, CancellationToken ct);
}

public sealed record TripPlanLockState(bool Locked, string? Reason, Guid? RequestId, Guid? EnquiryId = null)
{
    public static readonly TripPlanLockState Unlocked = new(false, null, null);
}

public interface ITripPlanLockReader
{
    Task<TripPlanLockState> GetAsync(ApplicationDbContext db, Guid tripId, CancellationToken ct = default);
}

public sealed record TaxiRequestChange(Guid RequestId, Guid TripId, Guid TravellerUserId, long Version, string State);

// What a closure changed, for the after-commit signals (matcher, freed drivers, realtime hints).
public sealed record TaxiClosureResult(IReadOnlyList<long> CityKeys, IReadOnlyList<Guid> FreedDriverListingIds, IReadOnlyList<TaxiRequestChange> Changes)
{
    public static readonly TaxiClosureResult Empty = new([], [], []);
}

// Entry points of MASTER_PLAN B2-8; each runs in the caller's transaction.
public interface ITaxiRequestClosure
{
    Task<TaxiClosureResult> ListingRemovedAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default);
    Task<TaxiClosureResult> ListingTakenDownAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default);
    Task<TaxiClosureResult> DriverOfflineAsync(ApplicationDbContext db, Guid driverListingId, CancellationToken ct = default);
    Task<TaxiClosureResult> TravellerAccountDeletedAsync(ApplicationDbContext db, Guid travellerUserId, CancellationToken ct = default);
    Task<TaxiClosureResult> UsersBlockedAsync(ApplicationDbContext db, Guid userAId, Guid userBId, CancellationToken ct = default);
}
