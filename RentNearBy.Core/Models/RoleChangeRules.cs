using System.Globalization;

namespace RentNearBy.Core.Models;

public static class RoleChangeRules
{
    public const int RejectedCooldownDays = 7;
    public const int ApprovedCooldownDays = 30;

    public const string LiveListingErrorType = "RoleChangeBlockedByLiveListing";

    public static string LiveListingBlockedMessage(string listingKind) => listingKind switch
    {
        ListingKinds.Room => "Live rooms or a pending Go Live request block this role change.",
        ListingKinds.Stay => "A stay under review blocks this role change.",
        ListingKinds.Taxi => "A taxi under review blocks this role change.",
        _ => "Live plots or a pending Go Live request block this role change.",
    };

    // India has no DST, so a fixed offset is exact.
    public static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    public static DateTime? NextRequestAllowedAt(string? status, DateTime? reviewedAt, DateTime utcNow)
    {
        if (reviewedAt is null) return null;

        var days = status == RoleChangeRequestStatuses.Approved ? ApprovedCooldownDays
            : status == RoleChangeRequestStatuses.Rejected ? RejectedCooldownDays
            : 0;
        if (days == 0) return null;

        var next = reviewedAt.Value.AddDays(days);
        return next > utcNow ? next : null;
    }

    public static string CooldownMessage(DateTime nextAllowedAtUtc)
    {
        var local = new DateTimeOffset(DateTime.SpecifyKind(nextAllowedAtUtc, DateTimeKind.Utc)).ToOffset(IstOffset);
        return $"You can send a new request after {local.ToString("d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture)}.";
    }
}
