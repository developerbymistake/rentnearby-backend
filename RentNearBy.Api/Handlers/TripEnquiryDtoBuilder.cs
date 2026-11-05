using System.Text.Json;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;

namespace RentNearBy.Api.Handlers;

public static class TripEnquiryDtoBuilder
{
    public const string DeletedTraveller = "Deleted user";
    public const string DeletedOwner = "This host left Bakhli";

    // Stay phones: the traveller's reaches the owner only on ConfirmedByBoth; the owner's reaches the traveller when
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
            travellerPhone = ownerViewer && bothConfirmed ? traveller.Phone : null;
            ownerPhone = travellerViewer && e.OwnerUserId != null && (owner.ContactVisible || bothConfirmed) ? owner.Phone : null;
            ownerWhatsApp = travellerViewer ? owner.WhatsApp : null;
        }

        var ordered = offers.OrderByDescending(o => o.CreatedAt).ToList();
        var open = Pending(ordered, TripOfferKinds.Offer);
        var accepted = e.AcceptedOfferId is { } acceptedId ? ordered.FirstOrDefault(o => o.Id == acceptedId) : null;

        var context = ReadContext(e.ContextJson);
        var phoneBooking = ReadPhoneBooking(e.PhoneBookingJson);

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
                Needs = context.Needs ?? [],
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
            PhoneBooking = phoneBooking == null ? null : new TripPhoneBookingDto
            {
                HostName = phoneBooking.HostName,
                HostPhone = travellerViewer ? phoneBooking.HostPhone : null,
                RoomName = phoneBooking.RoomName,
                Nights = phoneBooking.Nights,
                AgreedTotal = phoneBooking.AgreedTotal,
                AdvancePaid = phoneBooking.AdvancePaid,
                AdvanceAmount = phoneBooking.AdvanceAmount,
                Note = phoneBooking.Note,
                RecordedAt = phoneBooking.RecordedAt,
                UpdatedAt = phoneBooking.UpdatedAt,
            },
            HostConfirm = new TripHostConfirmDto
            {
                LinkSent = e.HostConfirmTokenHash != null,
                LinkCount = e.HostConfirmLinkCount,
                CanResendAt = travellerViewer ? CanResendAt(e) : null,
                AnsweredAt = e.HostConfirmAnsweredAt,
                Answer = e.HostConfirmAnswer,
                AnsweredVia = e.HostConfirmAnsweredVia,
                CanOverrideUntil = ownerViewer && e.HostConfirmAnsweredVia == TripHostConfirmVia.Link
                    ? e.HostConfirmAnsweredAt?.AddDays(TripExecutionRules.HostLinkOverrideDays)
                    : null,
            },
            Advance = new TripAdvanceDto
            {
                Status = e.AdvanceStatus,
                Amount = e.AdvanceAmount,
                HoldUntil = e.HoldUntil,
                AskedAt = e.AdvanceAskedAt,
                ProofSentAt = e.ProofSentAt,
                ReceivedAt = e.AdvanceReceivedAt,
                NotYetAt = e.AdvanceNotYetAt,
                LastHostReminderAt = e.LastHostReminderAt,
                NextHostReminderAt = TripAdvanceRules.NextHostReminder(e, nowUtc),
                ReminderCount = e.HostReminderCount,
            },
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
        };
    }

    // Batched: one offers query, one users query and (only with taxi rows) one taxi-contacts query for the whole list.
    public static async Task<List<TripEnquiryDto>> BuildManyAsync(
        IUnitOfWork unitOfWork, IReadOnlyList<TripEnquiry> enquiries, Guid viewerId, DateTime nowUtc)
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

        var tripDates = new Dictionary<Guid, (DateOnly? Start, DateOnly? End)>();
        var tripPlans = new Dictionary<Guid, TripPlan>();
        foreach (var tripId in enquiries.Where(e => e.TripId != null).Select(e => e.TripId!.Value).Distinct())
            if (await unitOfWork.Trips.GetByIdAsync(tripId) is { } trip)
            {
                tripDates[tripId] = (trip.StartDate, trip.EndDate);
                if (enquiries.Any(e => e.TripId == tripId && e.TravellerUserId == viewerId))
                    tripPlans[tripId] = TripPlanReader.Read(trip);
            }

        return enquiries.Select(e =>
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

    public static async Task<TripEnquiryDto> BuildOneAsync(IUnitOfWork unitOfWork, TripEnquiry e, Guid viewerId, DateTime nowUtc)
        => (await BuildManyAsync(unitOfWork, [e], viewerId, nowUtc))[0];

    public static TripOfferDto ToOfferDto(TripOffer o, TripEnquiry e)
    {
        var lines = ReadLines(o.LinesJson);
        return ToOfferDto(o, e, lines);
    }

    // CounterLines keeps sum(units x price) = guest price x units up to per-line rounding, so the unit average is the guest's price.
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
        NeedsEchoed = ReadStrings(o.NeedsEchoedJson),
        AdvanceAmount = o.AdvanceAmount,
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

    public static TripPhoneBooking? ReadPhoneBooking(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TripPhoneBooking>(json, TripJson.Options); }
        catch (JsonException) { return null; }
    }

    public static List<TripOfferLine> ReadLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<TripOfferLine>();
        try { return JsonSerializer.Deserialize<List<TripOfferLine>>(json, TripJson.Options) ?? new List<TripOfferLine>(); }
        catch (JsonException) { return new List<TripOfferLine>(); }
    }

    private static string[] ReadStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json, TripJson.Options) ?? []; }
        catch (JsonException) { return []; }
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

    private static DateTime? CanResendAt(TripEnquiry e)
    {
        if (e.Status != TripEnquiryStatuses.ConfirmedByTraveller || e.HostConfirmAnsweredAt != null) return null;
        if (e.HostConfirmTokenIssuedAt is not { } issued) return null;
        if (e.HostConfirmLinkCount >= TripExecutionRules.HostLinkMaxCount) return null;
        return issued.AddHours(TripExecutionRules.HostLinkResendHours);
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
