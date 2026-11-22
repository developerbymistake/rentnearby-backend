namespace RentNearBy.Core.Models.TaxiRequests;

// D-15 / FB-25: who can still be chosen. A new ring (and accepting it) needs an approved, live taxi. An offer the driver already
// accepted can still be selected and confirmed while a once-approved taxi is back under review after a vehicle edit; only a
// rejected re-review, a takedown or a delete closes it. One rule, mirrored by TaxiOfferRows.DriverSelectableSql.
public static class TaxiBookableRules
{
    public static bool CanRing(string status, bool isDeleted) => !isDeleted && status == TaxiStatuses.Approved;

    public static bool CanSelectExisting(string status, bool isDeleted, bool everApproved)
        => !isDeleted && (status == TaxiStatuses.Approved || (status == TaxiStatuses.PendingReview && everApproved));
}
