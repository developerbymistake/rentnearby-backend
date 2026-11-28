namespace RentNearBy.Core.Models.Owner;

[Flags]
public enum DeletionModes
{
    None = 0,
    // A foreign key from Users or the listing cascades (verified against the EF model by the guard test).
    Cascade = 1,
    // Deleted explicitly by ListingPurgeService / IOwnerListingPurge, AccountDeletionService or a participant statement.
    Purge = 2,
    // Deleted by an IAccountDeletionParticipant (buddy rows, money, notices).
    Participant = 4,
    // Kept on purpose for the other side or for records, with the user's name or id removed or never stored.
    Anonymised = 8,
}

public sealed record DeletionCoverage(string Table, DeletionModes Mode, string Reason);

// Contract 6.3, AU-26, R-4: every table that holds a user or listing key and what account deletion does to it.
// DeletionCoverageGuardTests fails when a table with such a key is missing here.
public static class AccountDeletionCoverage
{
    public static readonly IReadOnlyList<DeletionCoverage> Tables =
    [
        new("CalendarEntries", DeletionModes.Purge | DeletionModes.Cascade | DeletionModes.Anonymised,
            "Owner's rows: purge seam and Users cascade. Other owners' rows of his bookings: label blanked, TravellerUserId set null, kept to their 90-day clean-up."),
        new("StayBlockedRanges", DeletionModes.Purge | DeletionModes.Cascade, "Purge seam (soft stay delete does not cascade) and the StayListings cascade."),
        new("StaySpecialPrices", DeletionModes.Purge | DeletionModes.Cascade, "Purge seam (soft stay delete does not cascade) and the StayListings cascade."),
        new("FestivalReminderSent", DeletionModes.Purge | DeletionModes.Cascade, "Users cascade plus the explicit delete of OwnerAccountDeletion and the role purge."),
        new("OwnerWeeklyStats", DeletionModes.Purge, "Counts only; deleted by the purge seam with the listing."),
        new("UserNotices", DeletionModes.Cascade, "Users cascade; the role purge deletes the owner kinds explicitly."),
        new("OutboxMessages", DeletionModes.Purge, "Unsent rnb.push rows addressed to him are deleted by OwnerAccountDeletion (payload user id)."),
        new("TaxiDayOffs", DeletionModes.Purge | DeletionModes.Cascade,
            "Purge seam on soft taxi delete; TaxiListings cascade. The private reason goes with the row."),
        new("OwnerReminderRuns", DeletionModes.Anonymised, "LastListingId is a resume cursor, no person data; rows go after 30 days."),

        new("StayListings", DeletionModes.Purge | DeletionModes.Cascade, "ListingPurgeService deletes the rows; Users cascade is the net."),
        new("TaxiListings", DeletionModes.Purge | DeletionModes.Cascade, "ListingPurgeService deletes the rows; Users cascade is the net."),
        new("StayPhotos", DeletionModes.Purge | DeletionModes.Cascade, "ListingPurgeService deletes every row, hidden ones too (IgnoreQueryFilters); StayListings cascade."),
        new("StayRooms", DeletionModes.Purge | DeletionModes.Cascade, "ListingPurgeService deletes every row, hidden ones too (IgnoreQueryFilters); StayListings cascade."),
        new("TaxiRoutes", DeletionModes.Purge | DeletionModes.Cascade, "ListingPurgeService deletes every row, hidden ones too (IgnoreQueryFilters); TaxiListings cascade."),
        new("StayJobs", DeletionModes.Purge, "ListingPurgeService deletes the owner's jobs by UserId."),

        new("Sessions", DeletionModes.Purge | DeletionModes.Cascade, "Deleted first by AccountDeletionService; Users cascade."),
        new("DeviceTokens", DeletionModes.Purge | DeletionModes.Cascade, "Deleted first by AccountDeletionService so nothing is pushed meanwhile; Users cascade."),
        new("NotificationLogs", DeletionModes.Purge | DeletionModes.Cascade, "Deleted by AccountDeletionService; Users cascade."),
        new("NotificationEvents", DeletionModes.Cascade, "Users cascade (TargetUserId)."),
        new("NotificationReads", DeletionModes.Cascade, "Users cascade."),
        new("BannerDismissals", DeletionModes.Cascade, "Users cascade."),
        new("RoleChangeRequests", DeletionModes.Cascade, "Users cascade."),
        new("SavedJourneys", DeletionModes.Cascade, "Users cascade."),
        new("UserPlans", DeletionModes.Cascade, "Active rows are cancelled first, then the Users cascade deletes them."),
        new("Trips", DeletionModes.Cascade, "His own trips close (owners are told) and go with the Users cascade; an archived trip's SummaryJson (title, cities, place and stay names, counts) goes with its row."),
        new("TaxiRequests", DeletionModes.Cascade, "Users cascade on TravellerUserId; open requests are blockers before that."),
        new("Buddies", DeletionModes.Cascade | DeletionModes.Participant, "Users cascade on both ends; BuddyAccountDeletion runs first."),
        new("BuddyRequests", DeletionModes.Cascade | DeletionModes.Participant, "Users cascade on both ends; BuddyAccountDeletion runs first."),
        new("TripMembers", DeletionModes.Cascade | DeletionModes.Participant, "Users cascade on UserId; TripSharingAccountDeletion tells the other viewers."),

        new("DeletedAccountRecords", DeletionModes.Anonymised, "The deletion record itself (OriginalUserId), kept for the legal window by its own purge."),
        new("PlanPurchases", DeletionModes.Anonymised, "Financial record: UserId set null and AnonymisedAt stamped."),
        new("PlaySubscriptions", DeletionModes.Anonymised, "Kept detached and anonymised; stopped on Google's side by the reconciler."),
        new("CouponRedemptions", DeletionModes.Anonymised, "Financial record: UserId nulled by its SetNull foreign key."),
        new("ListingReports", DeletionModes.Anonymised, "Moderation record on the never-delete list; ids only, no copy of the person."),
        new("Conversations", DeletionModes.Anonymised, "Chat stays for the other side until the 90-day chat clean-up; ListingId is a plain reference."),
        new("Messages", DeletionModes.Anonymised, "Kept with their conversation for the other side until the 90-day chat clean-up; SenderId is a plain reference."),
        new("UserBlocks", DeletionModes.Participant, "BuddyAccountDeletion deletes every block he made or received (BlockerId or BlockedId)."),
        new("SavedJourneyLegs", DeletionModes.Anonymised, "Another traveller's leg keeps the taxi id of a removed listing (no person data); every reader treats a missing taxi as not available. His own legs go with his SavedJourneys."),
        new("TripEnquiries", DeletionModes.Anonymised, "Kept for the other side and closed by the closures; OwnerUserId / TravellerUserId set null (ListedStayTotal and its breakdown stay with the row)."),
        new("TripOffers", DeletionModes.Anonymised, "Kept with their enquiry for the other side until the enquiry clean-up; SenderUserId is a plain reference."),
        new("TripShortlistItems", DeletionModes.Anonymised, "Closed as Unavailable by the listing-removed closure; rows go after 30 days."),
        new("TaxiBookings", DeletionModes.Anonymised, "Kept for the other side; TravellerName / DriverName replaced by the deleted-user name."),
        new("TaxiRequestOffers", DeletionModes.Anonymised, "Closed by the listing-removed closure and kept with their request until its clean-up; no name stored."),
        new("TaxiRatings", DeletionModes.Anonymised, "Rating rows keep no name; on the never-delete list."),
        new("DriverDispatchStates", DeletionModes.Anonymised, "Fair-queue counters keyed by listing, no person data; the pool joins live listings only."),
        new("TripExpenses", DeletionModes.Anonymised | DeletionModes.Participant, "Kept for the other travellers' ledger, shown as a deleted user; MoneyAccountDeletion blocks while money is open."),
        new("TripExpenseShares", DeletionModes.Anonymised | DeletionModes.Participant, "Kept for the other travellers' ledger, shown as a deleted user."),
        new("TripExpenseRevisions", DeletionModes.Anonymised, "Edit history of shared expenses for the other travellers; ActorUserId is a plain reference."),
        new("TripPayments", DeletionModes.Anonymised | DeletionModes.Participant, "Kept for the other travellers' ledger; MoneyAccountDeletion blocks while a payment is open."),

        // Bakhli social (CONTRACT_SOCIAL 1.10); SocialDeletionParticipant.
        new("UserProfiles", DeletionModes.Cascade, "Users cascade."),
        new("ProfileIdentityAudit", DeletionModes.Cascade, "Users cascade."),
        new("Follows", DeletionModes.Cascade | DeletionModes.Participant, "SocialDeletionParticipant deletes both directions and lowers the other side's counters set-based; Users cascade."),
        new("TripJoinRequests", DeletionModes.Cascade | DeletionModes.Participant, "His waiting requests end Cancelled first (owners' lists refresh); Users and Trips cascade."),
        new("ChatRooms", DeletionModes.Participant, "His direct rooms are deleted; a trip room goes with the trip and his member row with the Users cascade."),
        new("ChatRoomMembers", DeletionModes.Cascade | DeletionModes.Participant, "Users cascade and the room delete."),
        new("ChatMessages", DeletionModes.Participant, "His direct rooms are deleted with their messages (room cascade); trip room messages expire in 24 hours."),
        new("MessageSnapshots", DeletionModes.Anonymised, "Kept to PurgeAt for the report; ids kept, his name replaced by Deleted account."),
        new("DiscoverHides", DeletionModes.Cascade, "Users cascade on both sides (hider and hidden)."),
        new("FollowCountDirty", DeletionModes.Cascade, "Users cascade."),
        new("FollowPushGroups", DeletionModes.Cascade, "Users cascade (receiver)."),

        // Stay quotation (ARCHITECTURE 2, 9.4): no guest data anywhere; images go with the user prefix delete (both delivery types).
        new("StayQuoteProfiles", DeletionModes.Cascade | DeletionModes.Purge,
            "Users cascade (bank blob included); the role change away from Stay Owner deletes the row and the quote images."),
        new("StayQuoteTermsAcceptances", DeletionModes.Cascade, "Users cascade."),
        new("StayQuoteCounters", DeletionModes.Cascade, "Users cascade; counts and sequences only."),
        new("StayQuoteIssues", DeletionModes.Cascade, "Users cascade; numbers only, purged 180 days after the first issue."),
    ];
}
