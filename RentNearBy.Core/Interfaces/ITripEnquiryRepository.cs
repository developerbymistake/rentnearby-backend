using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Core.Interfaces;

public interface ITripEnquiryRepository : IRepository<TripEnquiry>
{
    Task<TripEnquiry?> GetByIdTrackedAsync(Guid id);
    // Tracked.
    // AsNoTracking, newest first.
    Task<IReadOnlyList<TripEnquiry>> GetByTripAsync(Guid tripId);
    // Tracked.
    Task<IReadOnlyList<TripEnquiry>> GetOpenByTripAsync(Guid tripId, string kind);
    // Tracked.
    Task<TripEnquiry?> FindOpenAsync(Guid tripId, string kind, Guid listingId, short? day);
    // Tracked; the driver's open local row of this trip and city leg.
    Task<TripEnquiry?> FindOpenTaxiAsync(Guid tripId, Guid listingId, short cityLeg);
    // AsNoTracking; open rows of this traveller for the listing across every trip.
    Task<IReadOnlyList<TripEnquiry>> GetOpenByTravellerListingAsync(Guid travellerId, string kind, Guid listingId);
    // AsNoTracking; open rows of this traveller for any of the listings, one query.
    Task<IReadOnlyList<TripEnquiry>> GetOpenByTravellerListingsAsync(Guid travellerId, string kind, IReadOnlyCollection<Guid> listingIds);
    // AsNoTracking; rows this request key already created for the listings (any status).
    Task<IReadOnlyList<TripEnquiry>> GetByClientRequestAsync(Guid travellerId, Guid clientRequestId, IReadOnlyCollection<Guid> listingIds);
    // AsNoTracking; open first, else newest.
    Task<TripEnquiry?> GetLatestByConversationAsync(Guid conversationId);
    Task<Dictionary<Guid, TripEnquiry>> GetLatestByConversationsAsync(IReadOnlyCollection<Guid> conversationIds);
    Task<(IReadOnlyList<TripEnquiry> Items, bool HasMore)> GetOwnerPagedAsync(Guid ownerId, string bucket, int page, int pageSize);
    Task<TripOwnerCounts> GetOwnerCountsAsync(Guid ownerId);
    // Set-based conditional status change (timer); also bumps UpdatedAt. Returns rows affected.
    Task<int> TryTransitionAsync(Guid id, string[] fromStatuses, string toStatus, string? failReason, DateTime nowUtc);
    // AsNoTracking; unconfirmed rows with ExpiresAt <= now or CheckIn < today (IST), as a UNION of two range scans.
    Task<IReadOnlyList<TripEnquiry>> GetExpiredUnconfirmedAsync(DateTime nowUtc, DateOnly todayIst, int take);
    Task<Dictionary<Guid, TripPartyInfo>> LoadPartiesAsync(IReadOnlyCollection<Guid> userIds);
    // Names and the recipient's chat-conversation map for a push about this enquiry.
    Task<TripPushContext> LoadPushContextAsync(TripEnquiry e, Guid recipientUserId, int? total = null);

    // Driver Trip leads (Kind == Taxi, OwnerUserId == driverId); AsNoTracking, newest first.
    Task<(IReadOnlyList<TripEnquiry> Items, bool HasMore)> GetDriverPagedAsync(Guid driverId, string bucket, int page, int pageSize);
    Task<TripDriverCounts> GetDriverCountsAsync(Guid driverId);
    // Set-based: ConfirmedByBoth + Pending/ProofSent + HoldUntil <= now -> Failed/HoldExpired. Returns rows affected.
    Task<int> TryExpireHoldAsync(Guid id, DateTime nowUtc);
    // Set-based: LastHostReminderAt = now, HostReminderCount = nextCount when the count still equals expectedCount.
    Task<int> TryBumpReminderAsync(Guid id, int expectedCount, int nextCount, DateTime nowUtc);
    // AsNoTracking; holds due to expire.
    Task<IReadOnlyList<TripEnquiry>> GetDueHoldsAsync(DateTime nowUtc, int take);
    // AsNoTracking keyset page by Id: proof sent, hold still running, ProofSentAt + HostReminderCount x interval <= now
    // (the caller checks the exact HostReminderSchedule slot).
    Task<(IReadOnlyList<TripEnquiry> Items, Guid? Next)> GetDueReminderAsync(DateTime nowUtc, Guid? after, int take);
    // AsNoTracking; taxi FareSent rows past ExpiresAt.
    Task<IReadOnlyList<TripEnquiry>> GetExpiredFareSentAsync(DateTime nowUtc, int take);
    // Live taxi listings only: (ContactPhone, WhatsAppNumber) per id.
    Task<Dictionary<Guid, (string? Phone, string? WhatsApp)>> LoadTaxiContactsAsync(IReadOnlyCollection<Guid> taxiIds);

    // AsNoTracking, newest first; the enquiries of a page of trips in one query.
    Task<IReadOnlyList<TripEnquiry>> GetByTripsAsync(IReadOnlyCollection<Guid> tripIds);
    // AsNoTracking keyset page by Id; Asked, 24 h to 72 h old, not expired, trip neither muted nor Completed, reminder key
    // not logged yet. Next = null on the last page.
    Task<(IReadOnlyList<TripEnquiry> Items, Guid? Next)> GetNoReplyDueAsync(DateTime nowUtc, Guid? after, int take);
}
