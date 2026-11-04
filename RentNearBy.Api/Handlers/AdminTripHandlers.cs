using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Admin view of trip enquiries (stay and taxi). Routes are AdminOnly and deliberately not behind the trip flag.
public static class AdminTripHandlers
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private const int QueryMax = 40;
    private const int NotMeThreshold = 3;
    private static readonly TimeSpan NotMeWindow = TimeSpan.FromDays(30);

    // GET /admin/trips/enquiries
    public static async Task<IResult> GetEnquiries(
        string? kind, string? status, string? q, bool? notMe, Guid? districtId, int? page, int? pageSize,
        ApplicationDbContext db, IUnitOfWork unitOfWork)
    {
        if (!string.IsNullOrWhiteSpace(kind) && kind != TripEnquiryKinds.Stay && kind != TripEnquiryKinds.Taxi)
            return BadRequestResponse("Kind must be Stay or Taxi");
        var bucket = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().ToLowerInvariant();
        if (bucket is not ("open" or "ended" or "closed" or "all"))
            return BadRequestResponse("Status must be open, ended, closed or all");
        var term = q?.Trim();
        if (term is { Length: > QueryMax }) return BadRequestResponse("Search is too long");

        var pageNo = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var now = DateTime.UtcNow;
        var today = TripExecutionRules.TodayIst(now);

        var query = db.TripEnquiries.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(e => e.Kind == kind);
        // Live open = Open status and not yet checked out; ended = Open status past its check-out; closed = terminal.
        if (bucket == "open")
            query = query.Where(e => TripEnquiryStatuses.Open.Contains(e.Status) && (e.CheckOut == null || e.CheckOut >= today));
        else if (bucket == "ended")
            query = query.Where(e => TripEnquiryStatuses.Open.Contains(e.Status) && e.CheckOut != null && e.CheckOut < today);
        else if (bucket == "closed") query = query.Where(e => !TripEnquiryStatuses.Open.Contains(e.Status));

        // Journey leads have no trip, so a district filter leaves them out.
        if (districtId is { } district)
            query = query.Where(e => e.TripId != null && db.Trips.Any(t => t.Id == e.TripId && t.DistrictId == district));

        if (!string.IsNullOrEmpty(term))
        {
            if (term.All(char.IsAsciiDigit))
            {
                var phoneUsers = db.Users.Where(u => u.PhoneNumber.EndsWith(term)).Select(u => u.Id);
                query = query.Where(e =>
                    (e.TravellerUserId != null && phoneUsers.Contains(e.TravellerUserId.Value))
                    || (e.OwnerUserId != null && phoneUsers.Contains(e.OwnerUserId.Value)));
            }
            else
            {
                var lower = term.ToLower();
                var nameUsers = db.Users.Where(u => u.Name != null && u.Name.ToLower().Contains(lower)).Select(u => u.Id);
                query = query.Where(e =>
                    e.ListingName.ToLower().Contains(lower)
                    || (e.TravellerUserId != null && nameUsers.Contains(e.TravellerUserId.Value)));
            }
        }

        if (notMe == true)
        {
            var since = now - NotMeWindow;
            var flagged = db.TripEnquiries
                .Where(x => x.TravellerUserId != null && x.HostConfirmAnswer == TripHostConfirmAnswers.NotMe && x.HostConfirmAnsweredAt >= since)
                .GroupBy(x => x.TravellerUserId!.Value)
                .Where(g => g.Count() >= NotMeThreshold)
                .Select(g => g.Key);
            query = query.Where(e => e.TravellerUserId != null && flagged.Contains(e.TravellerUserId.Value));
        }

        var rows = await query
            .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id)
            .Skip((pageNo - 1) * size)
            .Take(size + 1)
            .ToListAsync();
        var hasMore = rows.Count > size;
        rows = rows.Take(size).ToList();

        var travellerIds = rows.Where(r => r.TravellerUserId != null).Select(r => r.TravellerUserId!.Value).Distinct().ToList();
        var parties = await unitOfWork.TripEnquiries.LoadPartiesAsync(
            travellerIds.Concat(rows.Where(r => r.OwnerUserId != null).Select(r => r.OwnerUserId!.Value)).Distinct().ToList());
        var notMeCounts = await NotMeCountsAsync(db, travellerIds, now);

        return OkResponse(new AdminTripEnquiryPageDto
        {
            Items = rows.Select(r => ToListDto(r, parties, notMeCounts)).ToList(),
            HasMore = hasMore,
        });
    }

    // GET /admin/trips/enquiries/{id}
    public static async Task<IResult> GetEnquiry(Guid id, ApplicationDbContext db, IUnitOfWork unitOfWork)
    {
        var e = await db.TripEnquiries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (e == null) return NotFoundResponse("Enquiry not found");
        return OkResponse(await BuildDetailAsync(db, unitOfWork, e));
    }

    // POST /admin/trips/enquiries/{id}/close { note }
    public static async Task<IResult> Close(
        Guid id, AdminCloseTripEnquiryRequest request, IValidator<AdminCloseTripEnquiryRequest> validator,
        ClaimsPrincipal principal, ApplicationDbContext db, IUnitOfWork unitOfWork, ITripChatService chat,
        ITripNotifier notifier, ILoggerFactory loggers)
    {
        var validation = await validator.ValidateAsync(request);
        if (!validation.IsValid) return BadRequestResponse(validation.Errors[0].ErrorMessage);

        if (!Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var adminId))
            return UnauthorizedResponse();

        var e = await unitOfWork.TripEnquiries.GetByIdTrackedAsync(id);
        if (e == null) return NotFoundResponse("Enquiry not found");
        if (!TripEnquiryStatuses.IsOpen(e.Status))
            return ErrorResponse(409, "This enquiry is closed.", TripEnquiryErrorTypes.TripEnquiryClosed);

        var now = DateTime.UtcNow;
        if (e.CheckOut is { } checkOut && checkOut < TripExecutionRules.TodayIst(now))
            return ErrorResponse(409, "This booking already ended.", TripEnquiryErrorTypes.EnquiryEnded);

        var posts = new List<TripChatPost>();
        await unitOfWork.BeginTransactionAsync();
        try
        {
            e.Status = TripEnquiryStatuses.Closed;
            e.FailReason = TripCloseReasons.AdminClosed;
            e.CancelReason = TripTextSafety.CleanOrNull(request.Note, TripExecutionRules.CancellationMax);
            e.CancelledAt = now;
            e.CancelledByUserId = adminId;
            e.UpdatedAt = now;
            await TripEnquiryOps.CloseOpenOfferAsync(db, chat, e, TripOfferStatuses.Superseded, TripCardStatuses.Superseded, posts, now);
            await TripEnquiryOps.PostSystemAsync(db, chat, e, null, TripSystemLines.AdminClosed, posts);
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.CommitTransactionAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await unitOfWork.RollbackTransactionAsync();
            return ErrorResponse(409, "This enquiry just changed.", TripEnquiryErrorTypes.ConcurrentUpdate);
        }
        catch
        {
            await unitOfWork.RollbackTransactionAsync();
            throw;
        }

        var logger = loggers.CreateLogger("TripAdmin");
        logger.LogInformation("trip.admin.close {EnquiryId} {AdminId}", e.Id, adminId);

        try { await chat.AfterCommitAsync(posts); }
        catch (Exception ex) { logger.LogError(ex, "trip.chat.broadcast_failed"); }

        var pushes = new List<TripPendingPush>
        {
            new(e, e.TravellerUserId, TripNotifications.TripEnquiryClosedByAdmin),
            new(e, e.OwnerUserId, TripNotifications.TripEnquiryClosedByAdmin),
        };
        await TripPushSender.SendAsync(pushes, unitOfWork.TripEnquiries, notifier, logger);

        return OkResponse(await BuildDetailAsync(db, unitOfWork, e));
    }

    // GET /admin/trips/problems?kind&tripId&page&pageSize
    public static async Task<IResult> GetProblems(
        string? kind, Guid? tripId, int? page, int? pageSize, ApplicationDbContext db, IUnitOfWork unitOfWork)
    {
        if (!string.IsNullOrWhiteSpace(kind) && !TripProblemKinds.All.Contains(kind))
            return BadRequestResponse("Kind must be DriverNoShow, HostNoRoom, RoadClosed or Other");

        var pageNo = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);

        var query = db.TripProblemLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(p => p.Kind == kind);
        if (tripId is { } id) query = query.Where(p => p.TripId == id);

        var rows = await query
            .OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            .Skip((pageNo - 1) * size)
            .Take(size + 1)
            .ToListAsync();
        var hasMore = rows.Count > size;
        rows = rows.Take(size).ToList();

        return OkResponse(new AdminTripProblemPageDto
        {
            Items = await BuildProblemsAsync(db, unitOfWork, rows),
            HasMore = hasMore,
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<Dictionary<Guid, int>> NotMeCountsAsync(ApplicationDbContext db, IReadOnlyCollection<Guid> travellerIds, DateTime now)
    {
        if (travellerIds.Count == 0) return new Dictionary<Guid, int>();
        var since = now - NotMeWindow;
        var rows = await db.TripEnquiries.AsNoTracking()
            .Where(x => x.TravellerUserId != null && travellerIds.Contains(x.TravellerUserId.Value)
                && x.HostConfirmAnswer == TripHostConfirmAnswers.NotMe && x.HostConfirmAnsweredAt >= since)
            .GroupBy(x => x.TravellerUserId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Id, r => r.Count);
    }

    private static AdminTripEnquiryDto ToListDto(
        TripEnquiry e, Dictionary<Guid, TripPartyInfo> parties, Dictionary<Guid, int> notMeCounts)
    {
        var dto = new AdminTripEnquiryDto();
        Fill(dto, e, parties, notMeCounts);
        return dto;
    }

    private static void Fill(
        AdminTripEnquiryDto dto, TripEnquiry e, Dictionary<Guid, TripPartyInfo> parties, Dictionary<Guid, int> notMeCounts)
    {
        var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        var traveller = e.TravellerUserId is { } t && parties.TryGetValue(t, out var tp) ? tp : null;
        var owner = e.OwnerUserId is { } o && parties.TryGetValue(o, out var op) ? op : null;

        dto.Id = e.Id;
        dto.Kind = e.Kind;
        dto.Status = e.Status;
        dto.FailReason = e.FailReason;
        dto.DisplayStatus = TripExecutionRules.DisplayStatus(e.Status, e.FailReason);
        dto.AdvanceStatus = e.AdvanceStatus;
        dto.ListingId = e.ListingId;
        dto.ListingName = e.ListingName;
        dto.City = context.City;
        dto.TravellerName = e.TravellerUserId == null
            ? TripEnquiryDtoBuilder.DeletedTraveller
            : FirstNonEmpty(traveller?.Name, context.TravellerName, "Guest");
        dto.TravellerPhone = traveller?.Phone;
        dto.OwnerName = e.OwnerUserId == null ? TripEnquiryDtoBuilder.DeletedOwner : FirstNonEmpty(owner?.Name, null, "Owner");
        dto.OwnerPhone = owner?.Phone;
        dto.CheckIn = e.CheckIn;
        dto.CheckOut = e.CheckOut;
        dto.Days = ReadDays(e.DaysJson);
        dto.AgreedTotal = e.AgreedTotal;
        dto.AgreedFare = e.AgreedFare;
        dto.DriverFare = e.DriverFare;
        dto.IsUrgent = e.IsUrgent;
        dto.NotMeCount30d = e.TravellerUserId is { } id && notMeCounts.TryGetValue(id, out var count) ? count : 0;
        dto.CreatedAt = e.CreatedAt;
        dto.UpdatedAt = e.UpdatedAt;
    }

    private static async Task<AdminTripEnquiryDetailDto> BuildDetailAsync(ApplicationDbContext db, IUnitOfWork unitOfWork, TripEnquiry e)
    {
        var userIds = new[] { e.TravellerUserId, e.OwnerUserId }.OfType<Guid>().Distinct().ToList();
        var parties = await unitOfWork.TripEnquiries.LoadPartiesAsync(userIds);
        var travellerIds = e.TravellerUserId is { } t ? new List<Guid> { t } : new List<Guid>();
        var notMeCounts = await NotMeCountsAsync(db, travellerIds, DateTime.UtcNow);
        var offers = (await unitOfWork.TripOffers.GetByEnquiriesAsync([e.Id])).TryGetValue(e.Id, out var list) ? list : new List<TripOffer>();

        var detail = new AdminTripEnquiryDetailDto();
        Fill(detail, e, parties, notMeCounts);

        var context = TripEnquiryDtoBuilder.ReadContext(e.ContextJson);
        var days = ReadDays(e.DaysJson);
        var phoneBooking = TripEnquiryDtoBuilder.ReadPhoneBooking(e.PhoneBookingJson);

        detail.Context = new TripEnquiryContextDto
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
        };
        detail.Offers = offers.OrderByDescending(o => o.CreatedAt).Select(o => TripEnquiryDtoBuilder.ToOfferDto(o, e)).ToList();
        detail.PhoneBooking = phoneBooking == null ? null : new AdminTripPhoneBookingDto
        {
            HostName = phoneBooking.HostName,
            HostPhone = phoneBooking.HostPhone,
            RoomName = phoneBooking.RoomName,
            Nights = phoneBooking.Nights,
            AgreedTotal = phoneBooking.AgreedTotal,
            AdvancePaid = phoneBooking.AdvancePaid,
            AdvanceAmount = phoneBooking.AdvanceAmount,
            Note = phoneBooking.Note,
            RecordedAt = phoneBooking.RecordedAt,
        };
        detail.HostConfirm = new AdminTripHostConfirmDto
        {
            Answer = e.HostConfirmAnswer,
            AnsweredVia = e.HostConfirmAnsweredVia,
            AnsweredAt = e.HostConfirmAnsweredAt,
            LinkCount = e.HostConfirmLinkCount,
            TokenExpiresAt = e.HostConfirmTokenExpiresAt,
        };
        detail.Advance = new AdminTripAdvanceDto
        {
            Status = e.AdvanceStatus,
            Amount = e.AdvanceAmount,
            HoldUntil = e.HoldUntil,
            AskedAt = e.AdvanceAskedAt,
            ProofSentAt = e.ProofSentAt,
            ReceivedAt = e.AdvanceReceivedAt,
            NotYetAt = e.AdvanceNotYetAt,
            ReminderCount = e.HostReminderCount,
            LastHostReminderAt = e.LastHostReminderAt,
        };
        detail.Taxi = e.Kind != TripEnquiryKinds.Taxi ? null : new AdminTripTaxiDto
        {
            ListedRate = e.ListedRateSnapshot,
            DriverFare = e.DriverFare,
            DriverNote = e.DriverNote,
            FareEditCount = e.DriverFareEditCount,
            AgreedFare = e.AgreedFare,
            TollExtra = e.TollExtra,
            PickupTime = e.PickupTime,
            PickupPlace = e.PickupPlace,
            Days = days,
        };
        detail.PhoneBookingsPaused = detail.NotMeCount30d >= NotMeThreshold;
        detail.TripId = e.TripId;
        detail.ConversationId = e.ConversationId;
        detail.ClosedNote = e.FailReason == TripCloseReasons.AdminClosed ? e.CancelReason : null;

        var logs = await db.TripProblemLogs.AsNoTracking()
            .Where(p => p.TripEnquiryId == e.Id)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
        detail.Problems = await BuildProblemsAsync(db, unitOfWork, logs);
        return detail;
    }

    private static async Task<List<AdminTripProblemDto>> BuildProblemsAsync(
        ApplicationDbContext db, IUnitOfWork unitOfWork, IReadOnlyList<TripProblemLog> logs)
    {
        if (logs.Count == 0) return new List<AdminTripProblemDto>();

        var tripIds = logs.Select(l => l.TripId).Distinct().ToList();
        var trips = await db.Trips.AsNoTracking().Where(t => tripIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var enquiryIds = logs.Where(l => l.TripEnquiryId != null).Select(l => l.TripEnquiryId!.Value).Distinct().ToList();
        var enquiries = await db.TripEnquiries.AsNoTracking().Where(e => enquiryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
        var userIds = trips.Values.Select(t => t.OwnerUserId)
            .Concat(enquiries.Values.Where(e => e.OwnerUserId != null).Select(e => e.OwnerUserId!.Value))
            .Distinct().ToList();
        var parties = await unitOfWork.TripEnquiries.LoadPartiesAsync(userIds);

        var list = new List<AdminTripProblemDto>();
        foreach (var log in logs)
        {
            trips.TryGetValue(log.TripId, out var trip);
            TripEnquiry? e = log.TripEnquiryId is { } eid && enquiries.TryGetValue(eid, out var row) ? row : null;
            var traveller = trip != null && parties.TryGetValue(trip.OwnerUserId, out var tp) ? tp : null;
            var owner = e?.OwnerUserId is { } oid && parties.TryGetValue(oid, out var op) ? op : null;
            var failReason = TripProblemKinds.FailReasonOf(log.Kind);

            list.Add(new AdminTripProblemDto
            {
                Id = log.Id,
                TripId = log.TripId,
                TripTitle = trip?.Title ?? string.Empty,
                City = trip?.CityName ?? string.Empty,
                Day = log.Day,
                Date = trip?.StartDate?.AddDays(log.Day - 1),
                Kind = log.Kind,
                Note = log.Note,
                Latitude = log.Latitude,
                Longitude = log.Longitude,
                EnquiryId = e?.Id,
                EnquiryKind = e?.Kind,
                ListingName = e?.ListingName,
                OwnerName = e == null ? null : e.OwnerUserId == null ? TripEnquiryDtoBuilder.DeletedOwner : FirstNonEmpty(owner?.Name, null, "Owner"),
                OwnerPhone = owner?.Phone,
                TravellerName = FirstNonEmpty(traveller?.Name, null, "Guest"),
                TravellerPhone = traveller?.Phone,
                Marked = e != null && failReason != null && e.Status == TripEnquiryStatuses.Failed && e.FailReason == failReason,
                CreatedAt = log.CreatedAt,
            });
        }
        return list;
    }

    private static int[]? ReadDays(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<int[]>(json, TripJson.Options); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string FirstNonEmpty(string? a, string? b, string last)
    {
        if (!string.IsNullOrWhiteSpace(a)) return a.Trim();
        if (!string.IsNullOrWhiteSpace(b)) return b.Trim();
        return last;
    }
}
