using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RentNearBy.Core.Entities;

namespace RentNearBy.Core.Models;

// One builder per push row (CONTRACT_PHASE1A 4.6). Uniform shape: (TripEnquiry e, TripPushContext ctx); the context comes from
// ITripEnquiryRepository.LoadPushContextAsync and carries the recipient, names, total and the recipient's full chat argument map.
public static class TripNotifications
{
    private const string UrgentPrefix = "Urgent · ";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── host side ────────────────────────────────────────────────────────────

    public static NotificationEvent TripEnquiryReceived(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripEnquiryReceived, "New trip enquiry",
            Join(" · ", Name(ctx.TravellerName, "A guest"), Dates(e), People(e)) + ".", ChatOrEnquiries(e, ctx));

    public static NotificationEvent TripOfferAccepted(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripOfferAccepted, "Offer accepted",
            $"{Name(ctx.TravellerName, "A guest")} accepted Rs {Money(ctx.Total ?? e.AgreedTotal)} for {Dates(e)}.", ChatOrEnquiries(e, ctx));

    public static NotificationEvent TripOfferDeclined(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripOfferDeclined, "Offer declined",
            $"{Name(ctx.TravellerName, "A guest")} declined your offer.", ChatOrEnquiries(e, ctx));

    public static NotificationEvent TripPhoneBookingRecorded(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripPhoneBookingRecorded, $"Booking recorded by {Name(ctx.TravellerName, "a guest")}",
            $"Says you agreed Rs {Money(ctx.Total ?? e.AgreedTotal)} for {Dates(e)}. Confirm or say not me.", Enquiries(e, ctx));

    public static NotificationEvent TripHostLinkAnswered(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripHostLinkAnswered, $"Booking marked {(ctx.Confirmed == true ? "confirmed" : "not me")} by link",
            $"{Name(ctx.TravellerName, "A guest")}, {Dates(e)}. Not you? Change it here.", Enquiries(e, ctx));

    public static NotificationEvent TripTravellerLeft(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripTravellerLeft, $"{Name(ctx.TravellerName, "A guest")} cancelled the trip",
            $"{Dates(e)} is no longer needed.", OwnerList(e, ctx));

    public static NotificationEvent TripTravellerCancelled(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripTravellerCancelled, "Booking cancelled",
            $"{Name(ctx.TravellerName, "A guest")} cancelled {Dates(e)}.", OwnerList(e, ctx));

    // ── traveller side ───────────────────────────────────────────────────────

    public static NotificationEvent TripOfferReceived(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripOfferReceived, $"{Listing(e, ctx)} sent an offer",
            $"Rs {Money(ctx.Total)} for {Dates(e)}. Valid {(e.IsUrgent ? "2 h" : "24 h")}.", Chat(e, ctx));

    public static NotificationEvent TripHostConfirmed(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripHostConfirmed, $"{Listing(e, ctx)} confirmed",
            $"Your booking for {Dates(e)} is confirmed by both.", Plan(e, ctx));

    public static NotificationEvent TripHostDenied(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripHostDenied, $"{Listing(e, ctx)} says not me",
            $"Pick another {KindWord(e)} for {Date(e)}.", Plan(e, ctx));

    public static NotificationEvent TripListingRemoved(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripListingRemoved, $"{Listing(e, ctx)} is no longer on Bakhli",
            $"Pick another {KindWord(e)} for {Date(e)}.", Plan(e, ctx));

    public static NotificationEvent TripEnquiryExpired(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripEnquiryExpired, $"No reply from {Listing(e, ctx)}",
            $"Ask another {AskWord(e)} or call.", Plan(e, ctx));

    public static NotificationEvent TripNotAvailable(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripNotAvailable, $"{Listing(e, ctx)} is not available",
            $"Ask another {AskWord(e)}.", Plan(e, ctx));

    public static NotificationEvent TripOwnerCancelled(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripOwnerCancelled, $"{Listing(e, ctx)} cancelled",
            $"Pick another {KindWord(e)} for {Date(e)}.", Plan(e, ctx));

    // -- phase 1b: advance (stay) --

    public static NotificationEvent TripAdvanceRequested(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripAdvanceRequested, $"{Listing(e, ctx)} asked for the advance",
            $"Rs {Money(e.AdvanceAmount)}, hold till {HoldTime(e)}. Pay the host directly.", Chat(e, ctx));

    public static NotificationEvent TripAdvanceProofSent(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripAdvanceProofSent,
            $"{Name(ctx.TravellerName, "A guest")} sent proof for Rs {Money(e.AdvanceAmount)}",
            $"Check your UPI app and mark it received. Hold ends {HoldTime(e)}.", ChatOrEnquiries(e, ctx));

    public static NotificationEvent TripAdvanceNotYet(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripAdvanceNotYet, "The host has not seen your advance yet",
            $"Check with him on WhatsApp before {HoldTime(e)}.", Chat(e, ctx));

    public static NotificationEvent TripAdvanceReceived(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripAdvanceReceived, "Advance received",
            $"{Listing(e, ctx)} says your Rs {Money(e.AdvanceAmount)} advance is received.", Chat(e, ctx));

    // One builder, two recipients: host wording when the recipient owns the listing.
    public static NotificationEvent TripHoldExpired(TripEnquiry e, TripPushContext ctx)
    {
        var toHost = e.OwnerUserId == ctx.RecipientUserId;
        var body = toHost
            ? $"You did not mark the advance of {Name(ctx.TravellerName, "the guest")} in time."
            : $"{Listing(e, ctx)} did not confirm the advance in time.";
        return Event(ctx.RecipientUserId, NotificationTypes.TripHoldExpired, e.IsUrgent, "Hold expired", body,
            toHost ? ChatOrEnquiries(e, ctx) : Chat(e, ctx));
    }

    // -- phase 1b: taxi --

    public static NotificationEvent TripLeadReceived(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripLeadReceived, "New trip lead",
            Join(" · ", Name(ctx.TravellerName, "A traveller"), Dates(e), People(e), PickupText(e)) + ".", Leads(e));

    public static NotificationEvent TripDriverFare(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripDriverFare, $"{Listing(e, ctx)} replied Rs {Money(e.DriverFare)}",
            $"Listed Rs {Money(TripLeadRules.ListedTotal(e.ListedRateSnapshot, DayCount(e)))}. Call to settle, then tap I booked.", Plan(e, ctx));

    public static NotificationEvent TripTaxiBookedByTraveller(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripTaxiBookedByTraveller, $"{Name(ctx.TravellerName, "A traveller")} booked you",
            $"{Dates(e)}, Rs {Money(e.AgreedFare)}. Confirm in Trip leads.", Leads(e));

    public static NotificationEvent TripBookingChanged(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripBookingChanged, $"Booking changed by {Name(ctx.TravellerName, "a traveller")}",
            $"{Dates(e)}, Rs {Money(e.AgreedFare)}. Confirm again in Trip leads.", Leads(e));

    public static NotificationEvent TripDriverConfirmed(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripDriverConfirmed, $"{Listing(e, ctx)} confirmed",
            $"Taxi for {Dates(e)} confirmed by both.", Plan(e, ctx));

    public static NotificationEvent TripDriverDenied(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripDriverDenied, $"{Listing(e, ctx)} says not me",
            $"Pick another taxi for {Dates(e)}.", Plan(e, ctx));

    // -- phase 1b: admin close, to the traveller or the owner by recipient --

    public static NotificationEvent TripEnquiryClosedByAdmin(TripEnquiry e, TripPushContext ctx)
    {
        var toTraveller = e.TravellerUserId == ctx.RecipientUserId;
        var note = string.IsNullOrWhiteSpace(e.CancelReason) ? string.Empty : e.CancelReason.Trim();
        var body = Join(" ", $"{Listing(e, ctx)}, {Dates(e)}.", note);
        return Event(ctx.RecipientUserId, NotificationTypes.TripEnquiryClosedByAdmin, e.IsUrgent, "Enquiry closed by Bakhli", body,
            toTraveller ? Plan(e, ctx) : OwnerList(e, ctx));
    }

    // -- phase 2: negotiation, reminders, problem --

    public static NotificationEvent TripCounterReceived(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripCounterReceived, $"{Name(ctx.TravellerName, "A guest")} proposed a price",
            $"Rs {Money(ctx.Total)} a night for {Dates(e)}.", ChatOrEnquiries(e, ctx));

    public static NotificationEvent TripRevisedOffer(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripRevisedOffer, $"{Listing(e, ctx)} revised the price",
            $"Rs {Money(e.AgreedTotal)} to Rs {Money(ctx.Total)}. Your price stays until you accept.", Chat(e, ctx));

    public static NotificationEvent TripRevisedKept(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripRevisedKept, "Revised offer not accepted",
            $"{Name(ctx.TravellerName, "A guest")} keeps Rs {Money(ctx.Total)}.", ChatOrEnquiries(e, ctx));

    public static NotificationEvent TripChangeRequest(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripChangeRequest, "Change request",
            $"{Name(ctx.TravellerName, "A guest")} asks for " + Join(" · ", Dates(e), People(e)) + ".", ChatOrEnquiries(e, ctx));

    public static NotificationEvent TripChangeAccepted(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripChangeAccepted, $"{Listing(e, ctx)} accepted your change",
            $"New total Rs {Money(ctx.Total)} for {Dates(e)}.", Chat(e, ctx));

    public static NotificationEvent TripHostNotConfirmed(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripHostNotConfirmed, $"{PhoneHostName(e) ?? Listing(e, ctx)} has not confirmed yet",
            "We sent him one link. You can call him.", Plan(e, ctx));

    public static NotificationEvent TripNoReply(TripEnquiry e, TripPushContext ctx) =>
        Traveller(e, ctx, NotificationTypes.TripNoReply, $"No reply yet from {Listing(e, ctx)}",
            $"Call or WhatsApp; or ask another {AskWord(e)}.", Plan(e, ctx));

    public static NotificationEvent TripProblemReported(TripEnquiry e, TripPushContext ctx) =>
        Host(e, ctx, NotificationTypes.TripProblemReported, $"Problem on {Name(ctx.TravellerName, "a guest")}'s trip",
            IsTaxi(e) ? "The driver did not come." : "The host could not give the room.", OwnerList(e, ctx));

    private static string? PhoneHostName(TripEnquiry e)
    {
        if (string.IsNullOrWhiteSpace(e.PhoneBookingJson)) return null;
        try
        {
            var name = JsonSerializer.Deserialize<TripPhoneBooking>(e.PhoneBookingJson, TripJson.Options)?.HostName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (JsonException) { return null; }
    }

    // ── formatting ───────────────────────────────────────────────────────────

    // "01-02 Nov", or "01 Nov - 02 Dec" across months. Taxi rows store CheckOut = last day + 1, so they show the last day.
    public static string Dates(TripEnquiry e)
    {
        if (!IsTaxi(e) || e.CheckIn is not { } a || e.CheckOut is not { } b) return DateRange(e.CheckIn, e.CheckOut);
        var last = b.AddDays(-1);
        return last <= a ? a.ToString("dd MMM", Inv) : DateRange(a, last);
    }

    public static string DateRange(DateOnly? checkIn, DateOnly? checkOut)
    {
        if (checkIn is null) return "your dates";
        var a = checkIn.Value;
        var b = checkOut ?? a.AddDays(1);
        if (a.Month == b.Month && a.Year == b.Year)
            return $"{a.ToString("dd", Inv)}-{b.ToString("dd MMM", Inv)}";
        return $"{a.ToString("dd MMM", Inv)} - {b.ToString("dd MMM", Inv)}";
    }

    public static string Date(TripEnquiry e) => e.CheckIn?.ToString("dd MMM", Inv) ?? "your dates";

    public static string People(TripEnquiry e)
    {
        var (adults, kids) = ReadPeople(e.ContextJson);
        if (adults is null) return string.Empty;
        var a = adults.Value == 1 ? "1 adult" : $"{adults.Value} adults";
        return kids is > 0 ? $"{a}, {kids.Value} {(kids.Value == 1 ? "kid" : "kids")}" : a;
    }

    private static (int? Adults, int? Kids) ReadPeople(string? contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson)) return (null, null);
        try
        {
            var node = JsonNode.Parse(contextJson);
            return (node?["adults"]?.GetValue<int>(), node?["kids"]?.GetValue<int>());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return (null, null);
        }
    }

    private static string Money(int? amount) => (amount ?? 0).ToString("N0", Inv);
    private static string Name(string? name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
    private static bool IsTaxi(TripEnquiry e) => e.Kind == TripEnquiryKinds.Taxi;
    private static string KindWord(TripEnquiry e) => IsTaxi(e) ? "taxi" : "stay";
    private static string AskWord(TripEnquiry e) => IsTaxi(e) ? "driver" : "stay";

    // Taxi rows name the driver, stay rows the stay; both fall back to the row snapshot.
    private static string Listing(TripEnquiry e, TripPushContext ctx)
    {
        if (IsTaxi(e) && !string.IsNullOrWhiteSpace(ctx.OwnerName)) return ctx.OwnerName.Trim();
        if (!string.IsNullOrWhiteSpace(ctx.ListingName)) return ctx.ListingName;
        if (!string.IsNullOrWhiteSpace(e.ListingName)) return e.ListingName;
        return IsTaxi(e) ? "Your driver" : "Your stay";
    }

    private static string HoldTime(TripEnquiry e) => e.HoldUntil is { } h ? TripDateText.TimeIst(h) : "the deadline";

    private static string PickupText(TripEnquiry e) =>
        TimeOnly.TryParseExact(e.PickupTime, "HH:mm", Inv, DateTimeStyles.None, out var t)
            ? $"pickup {t.ToString("h:mm tt", Inv).ToLowerInvariant()}"
            : string.Empty;

    private static int DayCount(TripEnquiry e)
    {
        if (string.IsNullOrWhiteSpace(e.DaysJson)) return 1;
        try { return Math.Max(1, JsonSerializer.Deserialize<int[]>(e.DaysJson)?.Length ?? 1); }
        catch (JsonException) { return 1; }
    }
    private static string Join(string sep, params string[] parts) => string.Join(sep, parts.Where(p => !string.IsNullOrEmpty(p)));

    // ── routes and event assembly ────────────────────────────────────────────

    private readonly record struct Target(string Route, string? ArgsJson);

    private static Target Chat(TripEnquiry e, TripPushContext ctx) => ctx.ChatArgsJson != null
        ? new(UserNotificationRoutes.ChatConversation, ctx.ChatArgsJson)
        : Plan(e, ctx);

    private static Target Enquiries(TripEnquiry e, TripPushContext ctx) =>
        new(UserNotificationRoutes.StayEnquiries, JsonSerializer.Serialize(new { enquiryId = e.Id }));

    private static Target Leads(TripEnquiry e) =>
        new(UserNotificationRoutes.TripLeads, JsonSerializer.Serialize(new { enquiryId = e.Id }));

    // Owner-side list: the chat when one exists, else the driver's Trip leads (taxi) or the stay Enquiries list.
    private static Target OwnerList(TripEnquiry e, TripPushContext ctx) =>
        ctx.ChatArgsJson != null ? Chat(e, ctx) : IsTaxi(e) ? Leads(e) : Enquiries(e, ctx);

    private static Target ChatOrEnquiries(TripEnquiry e, TripPushContext ctx) => ctx.ChatArgsJson != null ? Chat(e, ctx) : Enquiries(e, ctx);

    private static Target Plan(TripEnquiry e, TripPushContext ctx) => e.TripId is { } t
        ? new(UserNotificationRoutes.TripPlan, JsonSerializer.Serialize(new { tripId = t }))
        : new(UserNotificationRoutes.MyTrips, null);

    private static NotificationEvent Host(TripEnquiry e, TripPushContext ctx, string type, string title, string body, Target t) =>
        Event(ctx.RecipientUserId, type, e.IsUrgent, title, body, t);

    private static NotificationEvent Traveller(TripEnquiry e, TripPushContext ctx, string type, string title, string body, Target t) =>
        Event(ctx.RecipientUserId, type, e.IsUrgent, title, body, t);

    private static NotificationEvent Event(Guid target, string type, bool urgent, string title, string body, Target t) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = target,
        Type = type,
        Title = urgent ? UrgentPrefix + title : title,
        Body = body,
        ActionRoute = t.Route,
        ActionArgumentsJson = t.ArgsJson,
        CreatedAt = DateTime.UtcNow,
    };
}

// Trip-level reminders (no enquiry); all open the plan.
public static class TripReminderNotifications
{
    public static NotificationEvent StartsIn3Days(Guid userId, Guid tripId, string city, string summary) =>
        Build(userId, NotificationTypes.TripStartsIn3Days, $"{City(city)} in 3 days", summary, tripId);

    public static NotificationEvent StartsToday(Guid userId, Guid tripId, string body) =>
        Build(userId, NotificationTypes.TripStartsToday, "Your trip starts today", body, tripId);

    public static NotificationEvent MarkComplete(Guid userId, Guid tripId, string city) =>
        Build(userId, NotificationTypes.TripMarkComplete, $"How was {City(city)}?", "Mark the trip complete.", tripId);

    private static string City(string? city) => string.IsNullOrWhiteSpace(city) ? "Your trip" : city.Trim();

    private static NotificationEvent Build(Guid userId, string type, string title, string body, Guid tripId) => new()
    {
        Id = Guid.NewGuid(),
        TargetUserId = userId,
        Type = type,
        Title = title,
        Body = body,
        ActionRoute = UserNotificationRoutes.TripPlan,
        ActionArgumentsJson = JsonSerializer.Serialize(new { tripId }),
        CreatedAt = DateTime.UtcNow,
    };
}
