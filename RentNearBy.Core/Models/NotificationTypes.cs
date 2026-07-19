namespace RentNearBy.Core.Models;

// Discriminator for NotificationEvent.Type — plain constants, no DB lookup table (matches
// InquiryStatuses/ListingKinds/CoinTransactionReasons's established idiom; a lookup table only pays
// for itself if types need to be admin-configurable at runtime, which isn't the case here). Add a
// new const here (and a matching wire-value entry below) for each new notification category —
// nothing else about the schema changes.
public static class NotificationTypes
{
    public const string LeadAssigned = "LeadAssigned";

    // The lowercase-snake value sent as the FCM data payload's "notification_type" key — kept
    // paired here (not derived by naive case-conversion) so the PascalCase DB value and the wire
    // value can never silently diverge; notification_service.dart's _handleNotificationTap
    // switches on this exact string for its legacy per-type fallback.
    private static readonly Dictionary<string, string> WireValues = new()
    {
        [LeadAssigned] = "lead_assigned",
    };

    public static string ToWireValue(string type) => WireValues.TryGetValue(type, out var wire) ? wire : type;
}
