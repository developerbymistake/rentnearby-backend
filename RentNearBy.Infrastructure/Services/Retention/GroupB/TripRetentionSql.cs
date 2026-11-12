namespace RentNearBy.Infrastructure.Services.Retention.GroupB;

// Shared SQL fragments for trip retention. Parameters used: @cutoff @today.
public static class TripRetentionSql
{
    public const string OpenStatuses = "'Asked','Offered','Countered','ConfirmedByBoth','FareSent','BookedByTraveller'";

    public static string ClosedEnquiry(string a) =>
        $"""({a}."Status" IN ('NotAvailable','Expired','Declined','CancelledByTraveller','CancelledByOwner','Closed') OR ({a}."Status" = 'Failed' AND {a}."FailReason" IN ('DriverDenied','HoldExpired','ListingRemoved')))""";

    // The trip row is gone, Completed, or ended before today. EndDate NULL on an uncompleted trip counts as running.
    public static string TripNotRunning(string tripIdExpr) =>
        $"""NOT EXISTS (SELECT 1 FROM "Trips" tr WHERE tr."Id" = {tripIdExpr} AND tr."Status" <> 'Completed' AND (tr."EndDate" IS NULL OR tr."EndDate" >= @today))""";

    public static string EnquiryDeletable(string e) =>
        $"""
        {ClosedEnquiry(e)}
        AND {e}."AdvanceStatus" = 'None'
        AND {e}."ConfirmedByTravellerAt" IS NULL AND {e}."ConfirmedByOwnerAt" IS NULL AND {e}."BookedByTravellerAt" IS NULL
        AND {e}."AdvanceReceivedAt" IS NULL AND {e}."AcceptedOfferId" IS NULL
        AND NOT EXISTS (SELECT 1 FROM "ListingReports" r WHERE (r."TripEnquiryId" = {e}."Id" OR (r."ListingType" = 'TripEnquiry' AND r."ListingId" = {e}."Id")) AND (r."Status" = 'Pending' OR r."ResolvedAt" IS NULL OR r."ResolvedAt" >= @cutoff))
        AND NOT EXISTS (SELECT 1 FROM "TripProblemLogs" p WHERE p."TripEnquiryId" = {e}."Id")
        AND ({e}."TripId" IS NULL OR {TripNotRunning($"{e}.\"TripId\"")})
        AND NOT EXISTS (SELECT 1 FROM "TripEnquiries" o WHERE o."TripId" = {e}."TripId" AND o."StepKey" IS NOT DISTINCT FROM {e}."StepKey" AND o."Id" <> {e}."Id" AND o."Status" IN ({OpenStatuses}))
        """;
}
