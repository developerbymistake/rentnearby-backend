using System.Text.Json;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Core.Models.Owner;

// Source facts read by the Auto sync (contract 7.4, 8.1, AU-10, R-1). Plain classes: filled by one raw SQL read.
public sealed class EnquiryFacts
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? TripId { get; set; }
    public short? Day { get; set; }
    public Guid ListingId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid? TravellerUserId { get; set; }
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    public string? DaysJson { get; set; }
    public string? ContextJson { get; set; }
    public int? AgreedTotal { get; set; }
    public int? ListedStayTotal { get; set; }
    public string? FailReason { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? PickupPlace { get; set; }
    public string? OfferLinesJson { get; set; }
    public string? TravellerName { get; set; }
    public DateOnly? TripStart { get; set; }
}

public sealed class BookingFacts
{
    public Guid Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public Guid DriverListingId { get; set; }
    public Guid DriverUserId { get; set; }
    public Guid TravellerUserId { get; set; }
    public DateOnly DateFrom { get; set; }
    public DateOnly DateTo { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime BookedAt { get; set; }
    public string PickupCityName { get; set; } = string.Empty;
    public short Seats { get; set; }
    public string TravellerName { get; set; } = string.Empty;
    public string? DropCityName { get; set; }
    public bool? DropSameAsPickup { get; set; }
    public short? Travellers { get; set; }
    public string? TripSnapshotJson { get; set; }
    public string? SightseeingDaysJson { get; set; }
}

// What the sync writes for one source. Confirmed = upsert the row; otherwise cancel an existing row (money notes kept).
public sealed record AutoEntryPlan(
    string SourceType,
    Guid SourceId,
    bool Confirmed,
    Guid? OwnerUserId,
    string ListingKind,
    Guid ListingId,
    Guid? TravellerUserId,
    DateOnly DateFrom,
    DateOnly DateTo,
    string? FromPlace,
    string? ToPlace,
    string Label,
    string? Detail,
    int? ListedTotal,
    int? AgreedTotal,
    short? Guests,
    short? Seats,
    string? RoomNames,
    string? PlacesJson,
    string? CancelledBy,
    DateOnly? CancelledOn);

// Pure text and field rules of Auto entries. Dates only: never a time, never a fare.
public static class CalendarAutoText
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed class Line
    {
        public string? RoomName { get; set; }
        public int Units { get; set; }
        public int Guests { get; set; }
    }

    private sealed class Context
    {
        public int? Adults { get; set; }
        public int? Kids { get; set; }
        public string? TravellerName { get; set; }
        public string? PickupPlace { get; set; }
        public string? City { get; set; }
    }

    // The enquiry kinds the calendar follows: every stay enquiry, and a local taxi lead of a trip (F16).
    public static string? SourceTypeOf(string kind, Guid? tripId, short? day) => kind switch
    {
        TripEnquiryKinds.Stay => CalendarSourceTypes.StayEnquiry,
        TripEnquiryKinds.Taxi when tripId != null && day == null => CalendarSourceTypes.TaxiLead,
        _ => null,
    };

    public static string Guests(int n) => n == 1 ? "1 guest" : $"{n} guests";

    public static string SeatsText(int n) => $"{n} seats";

    public static string Travellers(int n) => n == 1 ? "1 traveller" : $"{n} travellers";

    // o_st_29: "Family Room · 2 guests".
    public static string? StayDetail(string? roomNames, int? guests)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(roomNames)) parts.Add(roomNames);
        if (guests is > 0) parts.Add(Guests(guests.Value));
        return parts.Count == 0 ? null : Cut(string.Join(" · ", parts), OwnerLimits.DetailMax);
    }

    // AU-10 lead text: "{PickupPlace or the lead city} · {N travellers}" (a lead stores no car size).
    public static string? LeadDetail(string? place, int? guests)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(place)) parts.Add(place.Trim());
        if (guests is > 0) parts.Add(Travellers(guests.Value));
        return parts.Count == 0 ? null : Cut(string.Join(" · ", parts), OwnerLimits.DetailMax);
    }

    public static string? CancelledByOfEnquiry(EnquiryFacts e, string sourceType)
    {
        if (e.Status == TripEnquiryStatuses.ConfirmedByBoth) return null;
        var host = sourceType == CalendarSourceTypes.TaxiLead ? CalendarCancelledBy.Driver : CalendarCancelledBy.Owner;
        if (e.Status == TripEnquiryStatuses.CancelledByTraveller) return CalendarCancelledBy.Traveller;
        if (e.Status == TripEnquiryStatuses.CancelledByOwner) return host;
        if (e.Status == TripEnquiryStatuses.Closed && e.FailReason == TripCloseReasons.AdminClosed) return CalendarCancelledBy.Admin;
        if (e.CancelledByUserId is { } by && by == e.TravellerUserId) return CalendarCancelledBy.Traveller;
        if (e.CancelledByUserId is { } owner && owner == e.OwnerUserId) return host;
        return CalendarCancelledBy.System;
    }

    public static string CancelledByOfBooking(string? cancelledBy) => cancelledBy switch
    {
        BookingCancelledBy.Traveller => CalendarCancelledBy.Traveller,
        BookingCancelledBy.Driver => CalendarCancelledBy.Driver,
        BookingCancelledBy.Admin => CalendarCancelledBy.Admin,
        _ => CalendarCancelledBy.System,
    };

    // null = nothing to write (not a calendar source, or a confirmed source without usable dates).
    public static AutoEntryPlan? ForEnquiry(EnquiryFacts e)
    {
        var sourceType = SourceTypeOf(e.Kind, e.TripId, e.Day);
        if (sourceType == null) return null;
        var confirmed = e.Status == TripEnquiryStatuses.ConfirmedByBoth;
        var context = ReadContext(e.ContextJson);
        var label = Cut(FirstText(e.TravellerName, context.TravellerName), OwnerLimits.LabelMax) ?? string.Empty;
        var cancelledOn = confirmed ? (DateOnly?)null : IstTime.TodayIst(e.CancelledAt ?? e.UpdatedAt);
        var cancelledBy = CancelledByOfEnquiry(e, sourceType);
        var contextGuests = (context.Adults ?? 0) + (context.Kids ?? 0);

        if (sourceType == CalendarSourceTypes.StayEnquiry)
        {
            if (e.CheckIn is not { } checkIn || e.CheckOut is not { } checkOut || checkOut <= checkIn)
                return confirmed ? null : Cancel(sourceType, e.Id, ListingKinds.Stay, e.ListingId, cancelledBy, cancelledOn);
            var lines = ReadLines(e.OfferLinesJson);
            var rooms = lines.Select(l => l.RoomName?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            var roomNames = rooms.Count == 0 ? null : Cut(string.Join(", ", rooms), OwnerLimits.RoomNamesMax);
            var lineGuests = lines.Sum(l => Math.Max(0, l.Guests));
            var guests = lineGuests > 0 ? lineGuests : contextGuests;
            return new AutoEntryPlan(sourceType, e.Id, confirmed, e.OwnerUserId, ListingKinds.Stay, e.ListingId, e.TravellerUserId,
                checkIn, checkOut, null, null, label, StayDetail(roomNames, guests), e.ListedStayTotal, e.AgreedTotal,
                Small(guests), null, roomNames, null, cancelledBy, cancelledOn);
        }

        var range = LeadRange(e.CheckIn, e.CheckOut, e.TripStart, e.DaysJson);
        if (range is not { } r)
            return confirmed ? null : Cancel(sourceType, e.Id, ListingKinds.Taxi, e.ListingId, cancelledBy, cancelledOn);
        var place = Cut(FirstText(e.PickupPlace, context.PickupPlace, context.City), OwnerLimits.PlaceMax);
        return new AutoEntryPlan(sourceType, e.Id, confirmed, e.OwnerUserId, ListingKinds.Taxi, e.ListingId, e.TravellerUserId,
            r.From, r.To, place, null, label, LeadDetail(place, contextGuests), null, null,
            Small(contextGuests), null, null, null, cancelledBy, cancelledOn);
    }

    public static AutoEntryPlan ForBooking(BookingFacts b)
    {
        var confirmed = BookingStates.Active.Contains(b.Status) || b.Status == BookingStates.Completed;
        var snapshot = TaxiTripSnapshot.FromJson(b.TripSnapshotJson);
        var from = Cut(FirstText(snapshot?.Pickup?.Name, b.PickupCityName), OwnerLimits.PlaceMax);
        var to = b.DropSameAsPickup == true ? Cut(FirstText(b.DropCityName, b.PickupCityName), OwnerLimits.PlaceMax) : Cut(FirstText(b.DropCityName), OwnerLimits.PlaceMax);
        var places = PlacesOf(snapshot, b.SightseeingDaysJson);
        var dateTo = b.DateTo >= b.DateFrom ? b.DateTo : b.DateFrom;
        return new AutoEntryPlan(CalendarSourceTypes.TaxiBooking, b.Id, confirmed, b.DriverUserId, ListingKinds.Taxi, b.DriverListingId, b.TravellerUserId,
            b.DateFrom, dateTo, from, to, Cut(b.TravellerName, OwnerLimits.LabelMax) ?? string.Empty,
            b.Seats > 0 ? SeatsText(b.Seats) : null, null, null,
            Small(b.Travellers ?? 0), b.Seats > 0 ? b.Seats : null, null,
            places.Count == 0 ? null : JsonSerializer.Serialize(places),
            confirmed ? null : CancelledByOfBooking(b.CancelledBy),
            confirmed ? null : IstTime.TodayIst(b.CancelledAt ?? b.BookedAt));
    }

    // A local lead stores CheckIn = first day and CheckOut = last day + 1 (DriverCalendar.LegacyRange); without CheckIn the first
    // chosen day of DaysJson counts from the trip start; no start date = skipped.
    public static (DateOnly From, DateOnly To)? LeadRange(DateOnly? checkIn, DateOnly? checkOut, DateOnly? tripStart, string? daysJson)
    {
        if (checkIn is { } from)
        {
            var to = checkOut is { } end && end > from ? end.AddDays(-1) : from;
            return (from, to);
        }
        if (tripStart is not { } start) return null;
        var days = ReadDays(daysJson);
        if (days.Count == 0) return null;
        return (start.AddDays(days.Min() - 1), start.AddDays(days.Max() - 1));
    }

    // R-1: ordered city names of the sightseeing days of the request snapshot, at most 20.
    public static List<string> PlacesOf(TaxiTripSnapshot? snapshot, string? sightseeingDaysJson)
    {
        if (snapshot?.Days is not { Count: > 0 } days) return [];
        var chosen = ReadDays(sightseeingDaysJson).ToHashSet();
        if (chosen.Count == 0) return [];
        return days.Where(d => chosen.Contains(d.Day)).OrderBy(d => d.Day)
            .Select(d => d.City?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Select(c => Cut(c, OwnerLimits.PlaceMax)!)
            .Distinct().Take(OwnerLimits.PlacesMaxCount).ToList();
    }

    public static string? Cut(string? text, int max)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length <= max ? t : t[..max].TrimEnd();
    }

    private static AutoEntryPlan Cancel(string sourceType, Guid id, string listingKind, Guid listingId, string? cancelledBy, DateOnly? cancelledOn)
        => new(sourceType, id, false, null, listingKind, listingId, null, default, default, null, null, string.Empty, null, null, null, null, null, null, null,
            cancelledBy ?? CalendarCancelledBy.System, cancelledOn);

    private static short? Small(int n) => n > 0 ? (short)Math.Min(n, short.MaxValue) : null;

    private static string? FirstText(params string?[] texts) => texts.Select(t => t?.Trim()).FirstOrDefault(t => !string.IsNullOrEmpty(t));

    private static List<Line> ReadLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Line>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static Context ReadContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Context();
        try { return JsonSerializer.Deserialize<Context>(json, Json) ?? new Context(); }
        catch (JsonException) { return new Context(); }
    }

    private static List<int> ReadDays(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return (JsonSerializer.Deserialize<List<int>>(json, Json) ?? []).Where(d => d >= 1).ToList(); }
        catch (JsonException) { return []; }
    }
}
