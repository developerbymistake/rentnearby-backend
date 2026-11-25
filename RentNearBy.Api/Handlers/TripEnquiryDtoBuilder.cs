using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Api.Handlers;

public static class TripEnquiryDtoBuilder
{
    public const string DeletedTraveller = "Deleted user";
    public const string DeletedOwner = "This host left Bakhli";

    // Stay phones: the traveller's reaches the owner while the row is open and she shared it at send (phoneShared), or on
    // ConfirmedByBoth; the owner's reaches the traveller when
    // contact is visible or on ConfirmedByBoth. The host phone typed into a phone booking is shown to the traveller only.
    // Taxi phones: the traveller's reaches the driver while the lead is open or closed less than 7 days ago (consent at send);
    // the traveller sees the taxi listing's own contact numbers.
    public static TripEnquiryDto Build(
        TripEnquiry e, IReadOnlyList<TripOffer> offers, TripPartyInfo traveller, TripPartyInfo owner, Guid viewerId, DateTime nowUtc,
        (string? Phone, string? WhatsApp)? taxiContact = null)
    {
        var ownerViewer = e.OwnerUserId is { } ownerId && ownerId == viewerId;
        var travellerViewer = e.TravellerUserId is { } travellerId && travellerId == viewerId;
        var bothConfirmed = e.Status == TripEnquiryStatuses.ConfirmedByBoth;
        var isTaxi = e.Kind == TripEnquiryKinds.Taxi;
        var days = ReadDays(e.DaysJson);
        var context = ReadContext(e.ContextJson);

        string? travellerPhone;
        string? ownerPhone;
        string? ownerWhatsApp;
        if (isTaxi)
        {
            var leadVisible = TripEnquiryStatuses.IsOpen(e.Status)
                || e.UpdatedAt >= nowUtc.AddDays(-TripLeadRules.PhoneVisibleAfterCloseDays);
            travellerPhone = ownerViewer && leadVisible ? traveller.Phone : null;
            ownerPhone = travellerViewer && e.OwnerUserId != null ? taxiContact?.Phone : null;
            ownerWhatsApp = travellerViewer && e.OwnerUserId != null ? taxiContact?.WhatsApp : null;
        }
        else
        {
            travellerPhone = StayTravellerPhone(e, context, ownerViewer) ? traveller.Phone : null;
            ownerPhone = travellerViewer && e.OwnerUserId != null && (owner.ContactVisible || bothConfirmed) ? owner.Phone : null;
            ownerWhatsApp = travellerViewer ? owner.WhatsApp : null;
        }

        var ordered = offers.OrderByDescending(o => o.CreatedAt).ToList();
        var open = Pending(ordered, TripOfferKinds.Offer);
        var accepted = e.AcceptedOfferId is { } acceptedId ? ordered.FirstOrDefault(o => o.Id == acceptedId) : null;
        var isStayAsked = !isTaxi && e.Status == TripEnquiryStatuses.Asked;

        return new TripEnquiryDto
        {
            Id = e.Id,
            TripId = e.TripId,
            Kind = e.Kind,
            ListingId = e.ListingId,
            ListingName = e.ListingName,
            ListingLatitude = e.ListingLatitude,
            ListingLongitude = e.ListingLongitude,
            Day = e.Day,
            Days = days,
            CityLeg = e.CityLeg,
            CheckIn = e.CheckIn,
            CheckOut = e.CheckOut,
            Nights = TripExecutionRules.Nights(e.CheckIn, e.CheckOut),
            ConversationId = e.ConversationId,
            Status = e.Status,
            FailReason = e.FailReason,
            DisplayStatus = TripExecutionRules.DisplayStatus(e.Status, e.FailReason),
            ExpiresAt = e.ExpiresAt,
            IsUrgent = e.IsUrgent,
            AgreedTotal = e.AgreedTotal,
            ListedStayTotal = ownerViewer && !isTaxi ? e.ListedStayTotal : null,
            ListedBreakdown = ownerViewer && !isTaxi ? ReadBreakdown(e.ListedStayBreakdownJson) : null,
            ListedPerNight = ListedPerNightFor(e, travellerViewer),
            AcceptedOffer = accepted == null ? null : ToOfferDto(accepted, e),
            OpenOffer = open == null ? null : ToOfferDto(open, e),
            OfferCount = ordered.Count,
            PendingCounter = Pending(ordered, TripOfferKinds.Counter) is { } counter ? ToOfferDto(counter, e) : null,
            PendingRevised = Pending(ordered, TripOfferKinds.Revised) is { } revised ? ToOfferDto(revised, e) : null,
            PendingChange = Pending(ordered, TripOfferKinds.ChangeRequest) is { } change ? ToOfferDto(change, e) : null,
            CounterCount = ordered.Count(o => o.Kind == TripOfferKinds.Counter),
            RevisedCount = ordered.Count(o => o.Kind == TripOfferKinds.Revised),
            Context = new TripEnquiryContextDto
            {
                City = context.City,
                Adults = context.Adults,
                Kids = context.Kids,
                Rooms = context.Rooms,
                Days = context.Days ?? new(),
                PickupTime = context.PickupTime,
                PickupPlace = context.PickupPlace,
                Text = context.Text,
                CityIndex = context.CityIndex,
                CityCount = context.CityCount,
                JourneyId = context.JourneyId,
                JourneyLegIndex = context.LegIndex,
            },
            IsSegment = TripSegmentRows.IsSegment(e),
            Taxi = isTaxi ? BuildTaxi(e, context, days) : null,
            Traveller = new TripPartyDto
            {
                Id = e.TravellerUserId,
                Name = e.TravellerUserId == null ? DeletedTraveller : NameOr(traveller.Name, context.TravellerName, "Guest"),
                Phone = travellerPhone,
            },
            Owner = new TripOwnerPartyDto
            {
                Id = e.OwnerUserId,
                Name = e.OwnerUserId == null ? DeletedOwner : NameOr(owner.Name, null, isTaxi ? "Driver" : "Host"),
                Phone = ownerPhone,
                WhatsApp = ownerWhatsApp,
            },
            CancelledBy = e.Status switch
            {
                TripEnquiryStatuses.CancelledByTraveller => "traveller",
                TripEnquiryStatuses.CancelledByOwner => "owner",
                _ => null,
            },
            CancelReason = e.CancelReason,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
            StepKey = e.StepKey,
            IsQuiet = TripQuietRules.IsQuiet(e, nowUtc),
            QuietAt = travellerViewer && isStayAsked ? TripQuietRules.QuietAt(e.CreatedAt, e.ExpiresAt) : null,
            EndReason = TripEndReasons.Of(e.Status, e.FailReason, ordered.Any(o => o.Status == TripOfferStatuses.Expired)),
            UndoUntil = travellerViewer && TripUndoRules.CanRestore(e, nowUtc) ? e.UpdatedAt.AddSeconds(TripUndoRules.UndoSeconds) : null,
            OffersLeft = ownerViewer && !isTaxi
                ? Math.Max(0, TripExecutionRules.MaxOffersPerEnquiry - TripExecutionRules.CountedOffers(ordered))
                : null,
            WaitingHours = ownerViewer && isStayAsked ? WaitingHoursOf(e.CreatedAt, nowUtc) : null,
        };
    }

    // Stay contact rule: an open row whose traveller shared her phone at send, or a booking confirmed by both. No WhatsApp.
    public static bool StayTravellerPhone(TripEnquiry e, TripEnquiryContext context, bool ownerViewer)
        => ownerViewer && (e.Status == TripEnquiryStatuses.ConfirmedByBoth
            || (TripEnquiryStatuses.IsOpen(e.Status) && context.PhoneShared == true));

    public static int WaitingHoursOf(DateTime createdAtUtc, DateTime nowUtc)
        => Math.Max(0, (int)Math.Floor((nowUtc - createdAtUtc).TotalHours));

    // Traveller view of a stay booking only: the newest share card since the ask, else the share mark with live values.
    public static TripOwnerShareDto? OwnerShare(
        TripEnquiry e, Guid viewerId, (string PayloadJson, DateTime At)? card, DateTime? markAt, (string? UpiId, string? WhatsApp)? live)
    {
        if (e.Kind != TripEnquiryKinds.Stay || e.Status != TripEnquiryStatuses.ConfirmedByBoth || e.TravellerUserId != viewerId) return null;
        if (card is { } c && ReadShare(c.PayloadJson) is { } fromCard)
            return new TripOwnerShareDto { UpiId = fromCard.UpiId, WhatsApp = fromCard.WhatsApp, SharedAt = c.At };
        if (markAt is { } at && live is { } l)
        {
            var upi = string.IsNullOrWhiteSpace(l.UpiId) ? null : l.UpiId.Trim();
            var wa = string.IsNullOrWhiteSpace(l.WhatsApp) ? null : l.WhatsApp.Trim();
            if (upi != null || wa != null) return new TripOwnerShareDto { UpiId = upi, WhatsApp = wa, SharedAt = at };
        }
        return null;
    }

    private static (string? UpiId, string? WhatsApp)? ReadShare(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Read(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
            var upi = Read("upiId");
            var wa = Read("whatsApp");
            return upi == null && wa == null ? null : (upi, wa);
        }
        catch (JsonException) { return null; }
    }

    public static string ShareMarkKey(Guid enquiryId) => $"share:{enquiryId}";

    // One cards query, one marks query and (only for marks without a card) one stays query; only with the viewer's stay bookings in the batch.
    private static async Task ApplyOwnerShareAsync(
        ApplicationDbContext db, IReadOnlyList<TripEnquiry> enquiries, List<TripEnquiryDto> dtos, Guid viewerId)
    {
        var booked = enquiries
            .Select((e, i) => (e, i))
            .Where(x => x.e.Kind == TripEnquiryKinds.Stay && x.e.Status == TripEnquiryStatuses.ConfirmedByBoth && x.e.TravellerUserId == viewerId)
            .ToList();
        if (booked.Count == 0) return;

        var conversationIds = booked.Where(x => x.e.ConversationId != null).Select(x => x.e.ConversationId!.Value).Distinct().ToList();
        var cards = conversationIds.Count == 0
            ? new List<ShareCardRow>()
            : await db.Messages.AsNoTracking()
                .Where(m => conversationIds.Contains(m.ConversationId) && m.Type == StayShareRules.MessageType)
                .Select(m => new ShareCardRow(m.ConversationId, m.CreatedAt, m.PayloadJson))
                .ToListAsync();
        var keys = booked.Select(x => ShareMarkKey(x.e.Id)).ToList();
        var marks = await db.TripPushLogs.AsNoTracking()
            .Where(p => keys.Contains(p.DedupeKey))
            .Select(p => new { p.DedupeKey, p.SentAt })
            .ToListAsync();

        (string, DateTime)? CardOf(TripEnquiry e) => e.ConversationId is { } cid
            && cards.Where(c => c.ConversationId == cid && c.CreatedAt >= e.CreatedAt).MaxBy(c => c.CreatedAt) is { } c
            ? (c.PayloadJson, c.CreatedAt) : null;

        var needLive = booked.Where(x => CardOf(x.e) == null && marks.Any(m => m.DedupeKey == ShareMarkKey(x.e.Id)))
            .Select(x => x.e.ListingId).Distinct().ToList();
        var live = needLive.Count == 0
            ? new List<ShareLiveRow>()
            : await db.StayListings.AsNoTracking()
                .Where(s => needLive.Contains(s.Id))
                .Select(s => new ShareLiveRow(s.Id, s.UpiId, s.WhatsAppNumber))
                .ToListAsync();

        foreach (var (e, i) in booked)
        {
            var mark = marks.FirstOrDefault(m => m.DedupeKey == ShareMarkKey(e.Id));
            var stay = live.FirstOrDefault(s => s.Id == e.ListingId);
            dtos[i].OwnerShare = OwnerShare(e, viewerId, CardOf(e), mark?.SentAt,
                stay == null ? null : (stay.UpiId, stay.WhatsApp));
        }
    }

    private sealed record ShareCardRow(Guid ConversationId, DateTime CreatedAt, string PayloadJson);
    private sealed record ShareLiveRow(Guid Id, string? UpiId, string? WhatsApp);

    // Batched: one offers query, one users query and (only with taxi rows) one taxi-contacts query for the whole list.
    // db (optional) fills ownerShare on the traveller's stay bookings.
    public static async Task<List<TripEnquiryDto>> BuildManyAsync(
        IUnitOfWork unitOfWork, IReadOnlyList<TripEnquiry> enquiries, Guid viewerId, DateTime nowUtc, ApplicationDbContext? db = null)
    {
        if (enquiries.Count == 0) return new List<TripEnquiryDto>();
        var offers = await unitOfWork.TripOffers.GetByEnquiriesAsync(enquiries.Select(e => e.Id).ToList());
        var userIds = enquiries
            .SelectMany(e => new[] { e.TravellerUserId, e.OwnerUserId })
            .Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var parties = await unitOfWork.TripEnquiries.LoadPartiesAsync(userIds);

        var taxiIds = enquiries
            .Where(e => e.Kind == TripEnquiryKinds.Taxi && e.TravellerUserId == viewerId)
            .Select(e => e.ListingId).Distinct().ToList();
        var contacts = taxiIds.Count > 0
            ? await unitOfWork.TripEnquiries.LoadTaxiContactsAsync(taxiIds)
            : new Dictionary<Guid, (string? Phone, string? WhatsApp)>();

        // One trips query for the whole list: dates for every row, PlanJson only for the viewer's own trips.
        var tripIds = enquiries.Where(e => e.TripId != null).Select(e => e.TripId!.Value).Distinct().ToList();
        var views = tripIds.Count > 0
            ? await unitOfWork.Trips.GetDatesAndOwnPlansAsync(tripIds, viewerId)
            : new Dictionary<Guid, TripEnquiryTripView>();
        var tripDates = new Dictionary<Guid, (DateOnly? Start, DateOnly? End)>();
        var tripPlans = new Dictionary<Guid, TripPlan>();
        foreach (var (tripId, view) in views)
        {
            tripDates[tripId] = (view.StartDate, view.EndDate);
            if (enquiries.Any(e => e.TripId == tripId && e.TravellerUserId == viewerId) && view.Plan() is { } plan)
                tripPlans[tripId] = plan;
        }

        var dtos = enquiries.Select(e =>
        {
            var dto = Build(
                e,
                offers.TryGetValue(e.Id, out var list) ? list : new List<TripOffer>(),
                Party(parties, e.TravellerUserId),
                Party(parties, e.OwnerUserId),
                viewerId, nowUtc,
                contacts.TryGetValue(e.ListingId, out var contact) ? contact : ((string? Phone, string? WhatsApp)?)null);
            if (e.TripId is { } id && tripDates.TryGetValue(id, out var dates))
            {
                dto.TripStartDate = dates.Start?.ToString("yyyy-MM-dd");
                dto.TripCheckOutLimit = dates.End is { } end ? TripDateRules.CheckOutLimit(end).ToString("yyyy-MM-dd") : null;
                if (tripPlans.TryGetValue(id, out var plan))
                    ApplyLegFlags(dto, e, plan, dates.Start, e.TravellerUserId == viewerId);
            }
            return dto;
        }).ToList();
        if (db != null) await ApplyOwnerShareAsync(db, enquiries, dtos, viewerId);
        return dtos;
    }

    // Wrong city and outside trip are a view of the row's dates against the current plan; a host or driver never sees them.
    public static void ApplyLegFlags(TripEnquiryDto dto, TripEnquiry e, TripPlan plan, DateOnly? start, bool travellerViewer)
    {
        dto.WrongCity = false;
        dto.WrongNights = 0;
        dto.WrongTotal = 0;
        dto.WrongFirstDay = null;
        dto.WrongDayCity = null;
        dto.OutsideTrip = false;
        if (!travellerViewer) return;

        var wrong = TripLegRules.WrongCity(e, plan, start);
        if (wrong.Any)
        {
            dto.WrongCity = true;
            dto.WrongNights = wrong.Wrong;
            dto.WrongTotal = wrong.Total;
            dto.WrongFirstDay = wrong.FirstWrongDay;
            dto.WrongDayCity = wrong.DayCity;
        }
        dto.OutsideTrip = TripLegRules.OutsideTrip(e, plan, start);
    }

    public static async Task<TripEnquiryDto> BuildOneAsync(
        IUnitOfWork unitOfWork, TripEnquiry e, Guid viewerId, DateTime nowUtc, ApplicationDbContext? db = null)
        => (await BuildManyAsync(unitOfWork, [e], viewerId, nowUtc, db))[0];

    public static TripOfferDto ToOfferDto(TripOffer o, TripEnquiry e)
    {
        var lines = ReadLines(o.LinesJson);
        return ToOfferDto(o, e, lines);
    }

    // CounterLines keeps sum(units x price) = guest price x units exactly, so the unit average is the guest's price.
    public static int? ProposedPerNight(TripOffer o, IReadOnlyList<TripOfferLine> lines)
    {
        if (o.Kind != TripOfferKinds.Counter) return null;
        long units = lines.Sum(l => (long)Math.Max(0, l.Units));
        if (units == 0) return null;
        return (int)Math.Round((double)lines.Sum(l => (long)l.Units * l.PricePerNight) / units, MidpointRounding.AwayFromZero);
    }

    private static TripOfferDto ToOfferDto(TripOffer o, TripEnquiry e, List<TripOfferLine> lines) => new()
    {
        Id = o.Id,
        Kind = o.Kind,
        CheckIn = o.CheckIn,
        CheckOut = o.CheckOut,
        Nights = o.Nights,
        Lines = lines,
        ProposedPerNight = ProposedPerNight(o, lines),
        Total = o.Total,
        Inclusions = o.Inclusions,
        BalanceNote = o.BalanceNote,
        CancellationText = o.CancellationText,
        GstInvoice = o.GstInvoice,
        Note = o.Note,
        Reason = o.Kind == TripOfferKinds.Revised ? o.Note : null,
        Adults = o.Adults,
        Kids = o.Kids,
        OldTotal = o.Status == TripOfferStatuses.Pending && o.Kind is TripOfferKinds.Revised or TripOfferKinds.ChangeRequest
            ? e.AgreedTotal
            : null,
        ValidUntil = o.ValidUntil,
        Status = o.Status,
        RespondedAt = o.RespondedAt,
        MessageId = o.MessageId,
        CreatedAt = o.CreatedAt,
        DatesDiffer = (o.CheckIn != null && o.CheckIn != e.CheckIn) || (o.CheckOut != null && o.CheckOut != e.CheckOut),
    };

    private static TripOffer? Pending(IReadOnlyList<TripOffer> newestFirst, string kind)
        => newestFirst.FirstOrDefault(o => o.Status == TripOfferStatuses.Pending && o.Kind == kind);

    public static TripEnquiryContext ReadContext(string? json) => TripEnquiryContexts.Read(json);

    // T5: the traveller sees the listed price per night only while his enquiry is Asked (an offer replaces it).
    public static int? ListedPerNightFor(TripEnquiry e, bool travellerViewer)
        => travellerViewer && e.Kind == TripEnquiryKinds.Stay && e.Status == TripEnquiryStatuses.Asked && e.ListedStayTotal is { } total
            ? StaySpecialPriceMath.PerNight(total, Math.Max(1, TripExecutionRules.Nights(e.CheckIn, e.CheckOut)))
            : null;

    public static List<ListedBreakdownLineDto>? ReadBreakdown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<ListedBreakdownLineDto>>(json, TripJson.Options); }
        catch (JsonException) { return null; }
    }

    public static List<TripOfferLine> ReadLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<TripOfferLine>();
        try { return JsonSerializer.Deserialize<List<TripOfferLine>>(json, TripJson.Options) ?? new List<TripOfferLine>(); }
        catch (JsonException) { return new List<TripOfferLine>(); }
    }

    public static int[]? ReadDays(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<int[]>(json, TripJson.Options); }
        catch (JsonException) { return null; }
    }

    private static TripTaxiDto BuildTaxi(TripEnquiry e, TripEnquiryContext context, int[]? days)
    {
        var dayCount = days is { Length: > 0 } ? days.Length : Math.Max(1, TripExecutionRules.Nights(e.CheckIn, e.CheckOut));
        return new TripTaxiDto
        {
            ListedRate = e.ListedRateSnapshot,
            ListedTotal = e.ListedRateSnapshot == null ? null : TripLeadRules.ListedTotal(e.ListedRateSnapshot, dayCount),
            DriverFare = e.DriverFare,
            DriverNote = e.DriverNote,
            DriverRepliedAt = e.DriverRepliedAt,
            FareEditCount = e.DriverFareEditCount,
            FareEditsLeft = TripLeadRules.EditsLeft(e.DriverFareEditCount),
            AgreedFare = e.AgreedFare,
            TollExtra = e.TollExtra,
            PickupTime = e.PickupTime ?? context.PickupTime,
            PickupPlace = e.PickupPlace ?? context.PickupPlace,
            BookedAt = e.BookedByTravellerAt,
        };
    }


    private static TripPartyInfo Party(Dictionary<Guid, TripPartyInfo> parties, Guid? id)
        => id is { } value && parties.TryGetValue(value, out var p) ? p : new TripPartyInfo(id, null, null, false, null);

    private static string NameOr(string? name, string? fallback, string last)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback.Trim();
        return last;
    }
}
