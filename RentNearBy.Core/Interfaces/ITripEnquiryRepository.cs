using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripEnquiryRepository : IRepository<TripEnquiry>
{
    Task<TripEnquiry?> GetByIdTrackedAsync(Guid id);
    // Tracked.
    Task<TripEnquiry?> GetByTokenHashAsync(string hash);
    // AsNoTracking, newest first.
    Task<IReadOnlyList<TripEnquiry>> GetByTripAsync(Guid tripId);
    // Tracked.
    Task<IReadOnlyList<TripEnquiry>> GetOpenByTripAsync(Guid tripId, string kind);
    // Tracked.
    Task<TripEnquiry?> FindOpenAsync(Guid tripId, string kind, Guid listingId, short? day);
    // AsNoTracking; open rows of this traveller for the listing across every trip.
    Task<IReadOnlyList<TripEnquiry>> GetOpenByTravellerListingAsync(Guid travellerId, string kind, Guid listingId);
    // AsNoTracking; open first, else newest.
    Task<TripEnquiry?> GetLatestByConversationAsync(Guid conversationId);
    Task<Dictionary<Guid, TripEnquiry>> GetLatestByConversationsAsync(IReadOnlyCollection<Guid> conversationIds);
    Task<(IReadOnlyList<TripEnquiry> Items, bool HasMore)> GetOwnerPagedAsync(Guid ownerId, string bucket, int page, int pageSize);
    Task<TripOwnerCounts> GetOwnerCountsAsync(Guid ownerId);
    Task<int> CountNotMeAsync(Guid travellerId, DateTime sinceUtc);
    // Set-based conditional status change (timer); also bumps UpdatedAt. Returns rows affected.
    Task<int> TryTransitionAsync(Guid id, string[] fromStatuses, string toStatus, string? failReason, DateTime nowUtc);
    // AsNoTracking; unconfirmed rows with ExpiresAt <= now or CheckIn < today (IST).
    Task<IReadOnlyList<TripEnquiry>> GetExpiredUnconfirmedAsync(DateTime nowUtc, DateOnly todayIst, int take);
    Task<Dictionary<Guid, TripPartyInfo>> LoadPartiesAsync(IReadOnlyCollection<Guid> userIds);
    // Names and the recipient's chat-conversation map for a push about this enquiry.
    Task<TripPushContext> LoadPushContextAsync(TripEnquiry e, Guid recipientUserId, int? total = null, bool? confirmed = null);

    // Driver Trip leads (Kind == Taxi, OwnerUserId == driverId); AsNoTracking, newest first.
    Task<(IReadOnlyList<TripEnquiry> Items, bool HasMore)> GetDriverPagedAsync(Guid driverId, string bucket, int page, int pageSize);
    Task<TripDriverCounts> GetDriverCountsAsync(Guid driverId);
    // Set-based: ConfirmedByBoth + Pending/ProofSent + HoldUntil <= now -> Failed/HoldExpired. Returns rows affected.
    Task<int> TryExpireHoldAsync(Guid id, DateTime nowUtc);
    // Set-based: LastHostReminderAt = now, HostReminderCount = expectedCount + 1 when the count still matches.
    Task<int> TryBumpReminderAsync(Guid id, int expectedCount, DateTime nowUtc);
    // AsNoTracking; holds due to expire.
    Task<IReadOnlyList<TripEnquiry>> GetDueHoldsAsync(DateTime nowUtc, int take);
    // AsNoTracking; proof sent, hold still running, last reminder at least 60 min ago.
    Task<IReadOnlyList<TripEnquiry>> GetDueReminderAsync(DateTime nowUtc, int take);
    // AsNoTracking; taxi FareSent rows past ExpiresAt.
    Task<IReadOnlyList<TripEnquiry>> GetExpiredFareSentAsync(DateTime nowUtc, int take);
    // Live taxi listings only: (ContactPhone, WhatsAppNumber) per id.
    Task<Dictionary<Guid, (string? Phone, string? WhatsApp)>> LoadTaxiContactsAsync(IReadOnlyCollection<Guid> taxiIds);
    // Same rule as CountNotMeAsync, grouped per traveller.
    Task<Dictionary<Guid, int>> CountNotMeByTravellersAsync(IReadOnlyCollection<Guid> travellerIds, DateTime sinceUtc);

    // AsNoTracking, newest first; the enquiries of a page of trips in one query.
    Task<IReadOnlyList<TripEnquiry>> GetByTripsAsync(IReadOnlyCollection<Guid> tripIds);
    // AsNoTracking; Asked, 24 h to 72 h old, not expired, trip neither muted nor Completed (joins Trips).
    Task<IReadOnlyList<TripEnquiry>> GetNoReplyDueAsync(DateTime nowUtc, int take);
    // AsNoTracking; ConfirmedByTraveller, host link unanswered for 24 h to 72 h, trip neither muted nor Completed.
    Task<IReadOnlyList<TripEnquiry>> GetHostNotConfirmedDueAsync(DateTime nowUtc, int take);
}
