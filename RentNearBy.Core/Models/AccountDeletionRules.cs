using System.Text.Json;
using System.Text.Json.Serialization;

namespace RentNearBy.Core.Models;

// Codes of the 409 AccountHasActiveItems answer of DELETE /account, in the order they are reported.
public static class AccountDeletionReasons
{
    public const string ActiveTaxiBooking = "ActiveTaxiBooking";
    public const string ActiveStayBooking = "ActiveStayBooking";
    public const string SelectedTaxiOffer = "SelectedTaxiOffer";
    public const string OpenTaxiRequest = "OpenTaxiRequest";
    public const string OpenStayEnquiry = "OpenStayEnquiry";
    public const string LiveListing = "LiveListing";
    // Reported by the buddy money participant (IAccountDeletionParticipant), not by Blocks(); {0} = trip title.
    public const string SharedTripOpenBalance = "SharedTripOpenBalance";
    public const string SharedTripOpenBalanceMessage = "You have money to settle on {0}.";
    // Reported first by SubscriptionDeletionGuard (all roles): an auto-renewing store subscription; one step, never a dead end.
    public const string ActiveSubscription = "ActiveSubscription";
    public const string ActiveSubscriptionMessage = "You have an active subscription. Cancel it in Google Play first, then delete your account.";
    public const string ActiveSubscriptionTitle = "Cancel your subscription first";
    public const string JustCancelledHint = " Just cancelled? Try again in a minute.";

    public static readonly IReadOnlyList<string> All =
        [ActiveTaxiBooking, ActiveStayBooking, SelectedTaxiOffer, OpenTaxiRequest, OpenStayEnquiry, LiveListing];

    public static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        [ActiveTaxiBooking] = "You have an active taxi booking. You can delete your account after it ends.",
        [ActiveStayBooking] = "You have an active stay booking. You can delete your account after it ends.",
        [SelectedTaxiOffer] = "A traveller has chosen you. Confirm or decline the trip first.",
        [OpenTaxiRequest] = "You have an open taxi request. Cancel it first.",
        [OpenStayEnquiry] = "You have an open stay enquiry. Close it first.",
        [LiveListing] = "Take your listing offline first.",
    };
}

public sealed record AccountActivity(
    bool ActiveTaxiBooking, bool ActiveStayBooking, bool SelectedTaxiOffer,
    bool OpenTaxiRequest, bool OpenStayEnquiry, bool LiveListing);

// AU-1: positional members with defaults, so every old two-argument construction still compiles. ArgsJson travels as the object "args".
public sealed record AccountDeletionBlock(
    string Code,
    string Message,
    string? Action = null,
    Guid? TargetId = null,
    string Side = AccountDeletionSides.Self,
    string? ReasonKey = null,
    string? Route = null,
    [property: JsonIgnore] string? ArgsJson = null,
    string? Title = null)
{
    public JsonElement? Args => string.IsNullOrEmpty(ArgsJson) ? null : JsonDocument.Parse(ArgsJson).RootElement.Clone();
}

public static class AccountDeletionSides
{
    public const string Self = "Self";
    public const string Host = "Host";
    public const string Driver = "Driver";
    public const string Traveller = "Traveller";
}

public static class AccountDeletionActions
{
    public const string OpenEnquiry = "OpenEnquiry";
    public const string OpenTripReplies = "OpenTripReplies";
    public const string OpenTaxiBooking = "OpenTaxiBooking";
    public const string OpenTaxiStatus = "OpenTaxiStatus";
    public const string OpenTaxiOffer = "OpenTaxiOffer";
    public const string OpenTrip = "OpenTrip";
    public const string GoOffline = "GoOffline";
    public const string SettleUp = "SettleUp";
    // args { manageUrl }: the exact Google Play subscription screen.
    public const string ManagePlay = "ManagePlay";
}

// AU-2: the first item (by CreatedAt, then Id) of one blocking kind and side, with the count of that kind and side.
// IsLead = a legacy trip lead (TripEnquiries) instead of a taxi request or booking; TripId rides along for traveller rows.
public sealed record AccountActivityItem(string Code, string Side, Guid Id, int Count, DateTime CreatedAt,
    Guid? TripId = null, bool IsLead = false, string? ListingKind = null);

public sealed record AccountDeletionResult(IReadOnlyList<AccountDeletionBlock> Reasons)
{
    public static readonly AccountDeletionResult Done = new([]);

    public bool Deleted => Reasons.Count == 0;
}

public static class AccountDeletionRules
{
    public const string ErrorType = "AccountHasActiveItems";
    public const string DeletedUserName = "Deleted user";

    public static IReadOnlyList<AccountDeletionBlock> Blocks(AccountActivity a)
    {
        var flags = new[]
        {
            a.ActiveTaxiBooking, a.ActiveStayBooking, a.SelectedTaxiOffer,
            a.OpenTaxiRequest, a.OpenStayEnquiry, a.LiveListing,
        };
        var blocks = new List<AccountDeletionBlock>();
        for (var i = 0; i < flags.Length; i++)
        {
            if (!flags[i]) continue;
            var code = AccountDeletionReasons.All[i];
            blocks.Add(new AccountDeletionBlock(code, AccountDeletionReasons.Messages[code]));
        }
        return blocks;
    }

    public static string ReasonKeyOf(string code) => "deleteBlock." + code;

    // AU-1: one row per blocking kind and side, in the table order (codes of AccountDeletionReasons.All, then side), each with the
    // button route and args of the first item. Items of the same kind and side are merged (counts added, the oldest is the target).
    public static IReadOnlyList<AccountDeletionBlock> Blocks(IEnumerable<AccountActivityItem> items)
    {
        var merged = items
            .GroupBy(i => (i.Code, i.Side))
            .Select(g =>
            {
                var first = g.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).First();
                return first with { Count = g.Sum(i => i.Count) };
            })
            .OrderBy(i => IndexOf(i.Code))
            .ThenBy(i => SideOrder(i.Side))
            .ToList();
        return merged.Select(Block).ToList();
    }

    public static AccountDeletionBlock Block(AccountActivityItem i)
    {
        var message = AccountDeletionReasons.Messages.TryGetValue(i.Code, out var m) ? m : i.Code;
        var (action, route, args) = Target(i);
        return new AccountDeletionBlock(i.Code, message, action, TargetIdOf(i), i.Side, ReasonKeyOf(i.Code), route, Json(args));
    }

    private static Guid TargetIdOf(AccountActivityItem i) => i switch
    {
        { Code: AccountDeletionReasons.ActiveStayBooking or AccountDeletionReasons.OpenStayEnquiry, Side: AccountDeletionSides.Traveller } => i.TripId ?? i.Id,
        { Code: AccountDeletionReasons.OpenTaxiRequest, IsLead: true } => i.TripId ?? i.Id,
        { Code: AccountDeletionReasons.ActiveTaxiBooking, Side: AccountDeletionSides.Traveller, IsLead: true } => i.TripId ?? i.Id,
        _ => i.Id,
    };

    private static (string Action, string Route, Dictionary<string, object?> Args) Target(AccountActivityItem i)
    {
        var count = i.Count;
        return (i.Code, i.Side) switch
        {
            (AccountDeletionReasons.ActiveStayBooking, AccountDeletionSides.Host) => (AccountDeletionActions.OpenEnquiry, UserNotificationRoutes.StayEnquiries,
                new() { ["enquiryId"] = i.Id, ["bucket"] = "Confirmed", ["count"] = count }),
            (AccountDeletionReasons.ActiveStayBooking, _) => (AccountDeletionActions.OpenTripReplies, UserNotificationRoutes.TripReplies,
                new() { ["tripId"] = i.TripId, ["enquiryId"] = i.Id, ["count"] = count }),
            (AccountDeletionReasons.OpenStayEnquiry, AccountDeletionSides.Host) => (AccountDeletionActions.OpenEnquiry, UserNotificationRoutes.StayEnquiries,
                new() { ["enquiryId"] = i.Id, ["bucket"] = "New", ["count"] = count }),
            (AccountDeletionReasons.OpenStayEnquiry, _) => (AccountDeletionActions.OpenTripReplies, UserNotificationRoutes.TripReplies,
                new() { ["tripId"] = i.TripId, ["enquiryId"] = i.Id, ["count"] = count }),
            (AccountDeletionReasons.ActiveTaxiBooking, AccountDeletionSides.Driver) => i.IsLead
                ? (AccountDeletionActions.OpenTaxiBooking, UserNotificationRoutes.TripLeads, new() { ["enquiryId"] = i.Id, ["count"] = count })
                : (AccountDeletionActions.OpenTaxiBooking, UserNotificationRoutes.TaxiBookings, new() { ["bookingId"] = i.Id, ["count"] = count }),
            (AccountDeletionReasons.ActiveTaxiBooking, _) => i.IsLead
                ? (AccountDeletionActions.OpenTaxiStatus, UserNotificationRoutes.TripPlan, new() { ["tripId"] = i.TripId ?? i.Id, ["count"] = count })
                : (AccountDeletionActions.OpenTaxiStatus, UserNotificationRoutes.TripTaxiStatus, new() { ["requestId"] = i.Id, ["count"] = count }),
            (AccountDeletionReasons.SelectedTaxiOffer, _) => (AccountDeletionActions.OpenTaxiOffer, UserNotificationRoutes.TaxiRequestDetail,
                new() { ["offerId"] = i.Id }),
            (AccountDeletionReasons.OpenTaxiRequest, _) => i.IsLead
                ? (AccountDeletionActions.OpenTrip, UserNotificationRoutes.TripPlan, new() { ["tripId"] = i.TripId ?? i.Id })
                : (AccountDeletionActions.OpenTaxiStatus, UserNotificationRoutes.TripTaxiStatus, new() { ["requestId"] = i.Id, ["tripId"] = i.TripId }),
            (AccountDeletionReasons.LiveListing, _) => (AccountDeletionActions.GoOffline, UserNotificationRoutes.OwnerHome,
                new() { ["tab"] = "home", ["listingKind"] = i.ListingKind }),
            _ => (AccountDeletionActions.OpenTrip, UserNotificationRoutes.MyTrips, new()),
        };
    }

    private static int IndexOf(string code)
    {
        for (var k = 0; k < AccountDeletionReasons.All.Count; k++)
            if (AccountDeletionReasons.All[k] == code) return k;
        return AccountDeletionReasons.All.Count;
    }

    private static int SideOrder(string side) => side switch
    {
        AccountDeletionSides.Host or AccountDeletionSides.Driver => 0,
        AccountDeletionSides.Traveller => 1,
        _ => 2,
    };

    private static string Json(Dictionary<string, object?> args) => JsonSerializer.Serialize(args, ArgsJsonOptions);

    private static readonly JsonSerializerOptions ArgsJsonOptions = new(JsonSerializerDefaults.Web);
}
