using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Infrastructure.Services.TaxiRequests;
using RentNearBy.Infrastructure.Services.TaxiRequests.DueActions;
using RentNearBy.Infrastructure.Services.TaxiRequests.Matching;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers.TaxiRequests;

// Pure rules and mappers of the admin taxi request API (contract section 11). The invite code is read by none of them.
public static class AdminTaxiRequestRules
{
    public const int StuckMinutes = 15;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int MaxSearchLength = 40;
    public const int MaxDueActions = 50;
    public const int AnswerWindowDays = 7;

    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(StuckMinutes);

    public static readonly IReadOnlyList<string> AnsweredStates = [OfferStates.Accepted, OfferStates.Selected, OfferStates.Confirmed];

    // Open or Collecting, nobody accepted, no wait for more drivers still running, and nothing changed for 15 minutes.
    // A wait that outlived its 10 minutes counts as no wait: its timer should have fired.
    private static readonly Expression<Func<TaxiRequest, StuckBounds, bool>> StuckShape = (r, b) =>
        (r.Status == RequestStates.Open || r.Status == RequestStates.Collecting)
        && r.AcceptedCount == 0
        && (r.QueueWaitStartedAt == null || r.QueueWaitStartedAt <= b.WaitOver)
        && r.UpdatedAt <= b.Quiet;

    private static readonly Func<TaxiRequest, StuckBounds, bool> StuckCompiled = StuckShape.Compile();

    public sealed record StuckBounds(DateTime Quiet, DateTime WaitOver)
    {
        public static StuckBounds At(DateTime nowUtc) => new(nowUtc - StuckAfter, nowUtc.AddSeconds(-TaxiDispatchRules.QueueWaitSeconds));
    }

    public static Expression<Func<TaxiRequest, bool>> StuckRequestPart(DateTime nowUtc)
    {
        var bounds = Expression.Constant(StuckBounds.At(nowUtc));
        var body = new ParameterSwap(StuckShape.Parameters[1], bounds).Visit(StuckShape.Body);
        return Expression.Lambda<Func<TaxiRequest, bool>>(body, StuckShape.Parameters[0]);
    }

    public static bool IsStuck(TaxiRequest request, bool hasRingingOffer, DateTime nowUtc)
        => !hasRingingOffer && StuckCompiled(request, StuckBounds.At(nowUtc));

    private sealed class ParameterSwap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    // Digits of a phone search ("+91 98" style input of digits and '+'); null = not a phone search, "" = nothing to match.
    public static string? PhoneDigits(string term)
        => term.All(c => char.IsAsciiDigit(c) || c == '+') ? term.TrimStart('+') : null;

    // Left Ringing without an answer for any reason (ring timed out, request ended, batch filled) inside the window.
    public static Expression<Func<TaxiRequestOffer, bool>> StoppedRingingUnanswered(DateTime since)
        => o => o.RespondedAt == null && o.Status != OfferStates.Ringing && o.ClosedAt != null && o.ClosedAt >= since;

    // Percent of the offers that stopped ringing that the driver answered; null when none stopped.
    public static int? AnsweredPercent(int answered, int unanswered)
    {
        var total = answered + unanswered;
        return total <= 0 ? null : (int)Math.Round(100.0 * answered / total, MidpointRounding.AwayFromZero);
    }

    // Nearest rank; null for no samples.
    public static double? Percentile95(IReadOnlyList<double> seconds)
    {
        if (seconds.Count == 0) return null;
        var sorted = seconds.OrderBy(x => x).ToList();
        var rank = (int)Math.Ceiling(0.95 * sorted.Count);
        return Math.Round(sorted[Math.Clamp(rank, 1, sorted.Count) - 1], 1);
    }

    public static double? RatingAverage(int sum, int count) => count <= 0 ? null : Math.Round((double)sum / count, 1);

    // Everything of ring readiness except the device token (contract section 6).
    public static readonly Expression<Func<TaxiListing, bool>> RingReadyBase = l =>
        l.RingReadyAt != null && l.RingNotificationsOn && l.RingMissStreak < TaxiDispatchRules.RingMissLimit;

    private static readonly Func<TaxiListing, bool> RingReadyCompiled = RingReadyBase.Compile();

    public static bool RingReady(TaxiListing listing, bool hasValidToken) => hasValidToken && RingReadyCompiled(listing);

    // null = unknown filter.
    public static IReadOnlyList<string>? RequestStatesOf(string? state) => (state ?? "active").Trim().ToLowerInvariant() switch
    {
        "active" => RequestStates.Active,
        "ended" => RequestStates.Final,
        "all" => RequestStates.All,
        _ => null,
    };

    public static IReadOnlyList<string>? BookingStatesOf(string? state) => (state ?? "active").Trim().ToLowerInvariant() switch
    {
        "active" => BookingStates.Active,
        "ended" => [BookingStates.Completed, BookingStates.Cancelled],
        "all" => BookingStates.All,
        _ => null,
    };

    public static int PageSize(int? size) => Math.Clamp(size ?? DefaultPageSize, 1, MaxPageSize);

    public static string EncodeCursor(DateTime at, Guid id) => $"{at.Ticks.ToString(CultureInfo.InvariantCulture)}_{id:N}";

    public static bool TryDecodeCursor(string? cursor, out DateTime at, out Guid id)
    {
        at = default;
        id = default;
        var parts = cursor?.Split('_');
        if (parts is not { Length: 2 }
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[1], "N", out id)) return false;
        at = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    public static AdminTaxiOfferCountsDto CountsOf(IEnumerable<string> offerStatuses)
    {
        var counts = new AdminTaxiOfferCountsDto();
        foreach (var status in offerStatuses)
        {
            counts.Total++;
            if (status == OfferStates.Ringing) counts.Ringing++;
            else if (status == OfferStates.Missed) counts.Missed++;
            else if (AnsweredStates.Contains(status)) counts.Accepted++;
        }
        return counts;
    }

    public static AdminTaxiRequestItemDto ToItem(TaxiRequest r, string tripName, string? travellerName, string? travellerPhone,
        AdminTaxiOfferCountsDto offers, DateTime nowUtc) => new()
    {
        Id = r.Id,
        TripId = r.TripId,
        TripName = tripName,
        State = r.Status,
        EndReason = r.EndReason,
        TravellerName = travellerName ?? "",
        TravellerPhone = travellerPhone,
        PickupName = r.PickupCityName,
        DateFrom = r.DateFrom,
        DateTo = r.DateTo,
        Offers = offers,
        CreatedAt = r.CreatedAt,
        AgeMinutes = Math.Max(0, (int)(nowUtc - r.CreatedAt).TotalMinutes),
    };

    public static AdminTaxiOfferDto ToOffer(TaxiRequestOffer o, string? driverName, string? driverPhone) => new()
    {
        OfferId = o.Id,
        DriverListingId = o.DriverListingId,
        DriverName = driverName ?? "",
        DriverPhone = driverPhone,
        State = o.Status,
        EndReason = o.EndReason,
        OfferedAt = o.OfferedAt,
        ExpiresAt = o.ExpiresAt,
        PushReceivedAt = o.PushReceivedAt,
        RespondedAt = o.RespondedAt,
    };

    public static AdminTaxiDueActionDto ToDueAction(DueAction d) => new()
    {
        Kind = d.Kind,
        State = d.State,
        DueAt = d.DueAt,
        Attempts = d.Attempts,
    };

    public static AdminTaxiBookingItemDto ToBooking(TaxiBooking b, string? driverPhone, string? travellerPhone, int? stars) => new()
    {
        BookingId = b.Id,
        RequestId = b.RequestId,
        State = b.Status,
        DriverName = b.DriverName,
        DriverPhone = driverPhone,
        TravellerName = b.TravellerName,
        TravellerPhone = travellerPhone,
        PickupName = string.IsNullOrEmpty(b.PickupCityName) ? null : b.PickupCityName,
        DateFrom = b.DateFrom,
        DateTo = b.DateTo,
        CancelledBy = b.CancelledBy,
        CancelChip = b.CancelChip,
        Stars = stars,
        BookedAt = b.BookedAt,
    };

    public static AdminTaxiRequestDetailDto ToDetail(TaxiRequest r, string tripName, string? travellerName, string? travellerPhone,
        IEnumerable<AdminTaxiOfferDto> offers, AdminTaxiBookingItemDto? booking, IEnumerable<AdminTaxiDueActionDto> dueActions) => new()
    {
        Id = r.Id,
        TripId = r.TripId,
        TripName = tripName,
        State = r.Status,
        EndReason = r.EndReason,
        TravellerName = travellerName ?? "",
        TravellerPhone = travellerPhone,
        PickupName = r.PickupCityName,
        DropName = r.DropCityName,
        Sections = new TaxiSectionsDto { Pickup = r.WantPickup, Sightseeing = r.WantSightseeing, Drop = r.WantDrop },
        Seats = r.Seats,
        Travellers = r.Travellers,
        DateFrom = r.DateFrom,
        DateTo = r.DateTo,
        CreatedAt = r.CreatedAt,
        CanCancel = RequestStates.IsActive(r.Status),
        AdminNote = r.AdminNote,
        Offers = offers.ToList(),
        Booking = booking,
        DueActions = dueActions.ToList(),
    };
}

// Every EF query of AD1 to AD6 before its terminal operator, so tests can translate each one to SQL without a database.
public static class AdminTaxiRequestQueries
{
    public sealed record PendingSummary(int Count, DateTime Oldest);

    public sealed record DriverContact(Guid Id, string? ContactPhone, string? Name, string? PhoneNumber);

    public static IQueryable<TaxiRequest> Requests(ApplicationDbContext db, IReadOnlyList<string> states, long? cityKey, string? term)
    {
        var query = db.Set<TaxiRequest>().AsNoTracking().Where(r => states.Contains(r.Status));
        if (cityKey is { } city) query = query.Where(r => r.DispatchCityKey == city);
        if (string.IsNullOrEmpty(term)) return query;
        var digits = AdminTaxiRequestRules.PhoneDigits(term);
        if (digits is null)
        {
            var lower = term.ToLower();
            return query.Where(r => db.Users.Any(u => u.Id == r.TravellerUserId && u.Name != null && u.Name.ToLower().Contains(lower)));
        }
        return digits.Length == 0 ? query : query.Where(r => db.Users.Any(u => u.Id == r.TravellerUserId && u.PhoneNumber.EndsWith(digits)));
    }

    // Newest first, ties by id; the cursor row itself is excluded. take + 1 rows tell whether a next page exists.
    public static IQueryable<TaxiRequest> RequestPage(IQueryable<TaxiRequest> query, (DateTime At, Guid Id)? after, int take)
    {
        if (after is var (at, id)) query = query.Where(r => r.CreatedAt < at || (r.CreatedAt == at && r.Id.CompareTo(id) > 0));
        return query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).Take(take + 1);
    }

    public static IQueryable<TaxiBooking> BookingPage(IQueryable<TaxiBooking> query, (DateTime At, Guid Id)? after, int take)
    {
        if (after is var (at, id)) query = query.Where(b => b.BookedAt < at || (b.BookedAt == at && b.Id.CompareTo(id) > 0));
        return query.OrderByDescending(b => b.BookedAt).ThenBy(b => b.Id).Take(take + 1);
    }

    public static IQueryable<TaxiBooking> Bookings(ApplicationDbContext db, IReadOnlyList<string> states)
        => db.Set<TaxiBooking>().AsNoTracking().Where(b => states.Contains(b.Status));

    public static IQueryable<DriverContact> DriverContacts(ApplicationDbContext db, IReadOnlyList<Guid> listingIds)
        => db.TaxiListings.AsNoTracking().Where(l => listingIds.Contains(l.Id))
            .Select(l => new DriverContact(l.Id, l.ContactPhone, l.User.Name, l.User.PhoneNumber));

    public static IQueryable<TaxiRequest> Stuck(ApplicationDbContext db, DateTime now)
    {
        var offers = db.Set<TaxiRequestOffer>().AsNoTracking();
        return db.Set<TaxiRequest>().AsNoTracking().Where(AdminTaxiRequestRules.StuckRequestPart(now))
            .Where(r => !offers.Any(o => o.RequestId == r.Id && o.Status == OfferStates.Ringing));
    }

    public static IQueryable<PendingSummary> PendingDueActions(ApplicationDbContext db)
        => db.Set<DueAction>().AsNoTracking().Where(d => d.State == DueActionStates.Pending)
            .GroupBy(_ => 1).Select(g => new PendingSummary(g.Count(), g.Min(d => d.DueAt)));

    public static IQueryable<PendingSummary> PendingOutbox(ApplicationDbContext db)
        => db.OutboxMessages.AsNoTracking().Where(m => m.PublishedAt == null)
            .GroupBy(_ => 1).Select(g => new PendingSummary(g.Count(), g.Min(m => m.CreatedAt)));

    public static IQueryable<TaxiListing> RingReadyDrivers(ApplicationDbContext db)
        => LiveDrivers(db).Where(AdminTaxiRequestRules.RingReadyBase)
            .Where(l => db.DeviceTokens.Any(t => t.UserId == l.UserId && t.IsValid));

    public static IQueryable<TaxiListing> DriversMissingRings(ApplicationDbContext db)
        => LiveDrivers(db).Where(l => l.RingMissStreak >= TaxiDispatchRules.RingMissLimit);

    private static IQueryable<TaxiListing> LiveDrivers(ApplicationDbContext db)
        => db.TaxiListings.AsNoTracking().Where(l => l.Status == TaxiStatuses.Approved && !l.IsDeleted);

    // Contract 11: of the offers that stopped ringing in the window, the ones the driver answered (accepted or declined).
    public static IQueryable<TaxiRequestOffer> AnsweredOffers(ApplicationDbContext db, DateTime since)
        => db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.RespondedAt != null && o.RespondedAt >= since);

    public static IQueryable<TaxiRequestOffer> UnansweredOffers(ApplicationDbContext db, DateTime since)
        => db.Set<TaxiRequestOffer>().AsNoTracking().Where(AdminTaxiRequestRules.StoppedRingingUnanswered(since));

    // AD6 driver detail.
    public static IQueryable<TaxiRequestOffer> DriverMissedOffers(ApplicationDbContext db, Guid listingId)
        => db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.DriverListingId == listingId && o.Status == OfferStates.Missed);

    public static IQueryable<TaxiRequestOffer> DriverAnsweredOffers(ApplicationDbContext db, Guid listingId)
        => db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.DriverListingId == listingId && o.RespondedAt != null
            && o.Status != OfferStates.Declined && o.Status != OfferStates.Missed);
}

// AD1 to AD5. Read paths only select; the one write (cancel) goes through TaxiRequestService.CancelByAdminAsync.
public static class AdminTaxiRequestHandlers
{
    private static readonly string[] OpenDueStates = [DueActionStates.Pending, DueActionStates.Running, DueActionStates.Failed];

    private static IResult Fail(TaxiRequestError error) => ErrorResponse(error.Status, error.Message, error.Type, error.Data);

    private static bool TryCursor(string? cursor, out (DateTime At, Guid Id)? after)
    {
        after = null;
        if (string.IsNullOrEmpty(cursor)) return true;
        if (!AdminTaxiRequestRules.TryDecodeCursor(cursor, out var at, out var id)) return false;
        after = (at, id);
        return true;
    }

    // GET /admin/taxi-requests
    public static async Task<IResult> GetRequests(
        string? state, long? cityKey, string? search, string? cursor, int? size, ApplicationDbContext db, CancellationToken ct)
    {
        var states = AdminTaxiRequestRules.RequestStatesOf(state);
        if (states is null) return BadRequestResponse("State must be active, ended or all");
        var term = search?.Trim();
        if (term is { Length: > AdminTaxiRequestRules.MaxSearchLength }) return BadRequestResponse("Search is too long");
        var take = AdminTaxiRequestRules.PageSize(size);
        if (!TryCursor(cursor, out var after)) return BadRequestResponse("Cursor is not valid");

        var rows = await AdminTaxiRequestQueries.RequestPage(AdminTaxiRequestQueries.Requests(db, states, cityKey, term), after, take).ToListAsync(ct);
        var more = rows.Count > take;
        rows = rows.Take(take).ToList();

        var ids = rows.Select(r => r.Id).ToList();
        var tripIds = rows.Select(r => r.TripId).Distinct().ToList();
        var userIds = rows.Select(r => r.TravellerUserId).Distinct().ToList();
        var titles = await db.Trips.AsNoTracking().Where(t => tripIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Title, ct);
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => new { u.Name, u.PhoneNumber }, ct);
        var offers = (await db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => ids.Contains(o.RequestId))
            .Select(o => new { o.RequestId, o.Status }).ToListAsync(ct))
            .GroupBy(o => o.RequestId)
            .ToDictionary(g => g.Key, g => AdminTaxiRequestRules.CountsOf(g.Select(o => o.Status)));

        var now = DateTime.UtcNow;
        var items = rows.Select(r =>
        {
            users.TryGetValue(r.TravellerUserId, out var u);
            return AdminTaxiRequestRules.ToItem(r, titles.GetValueOrDefault(r.TripId) ?? "", u?.Name, u?.PhoneNumber,
                offers.GetValueOrDefault(r.Id) ?? new AdminTaxiOfferCountsDto(), now);
        }).ToList();

        return OkResponse(new AdminTaxiRequestListDto
        {
            Items = items,
            NextCursor = more && rows.Count > 0 ? AdminTaxiRequestRules.EncodeCursor(rows[^1].CreatedAt, rows[^1].Id) : null,
        });
    }

    // GET /admin/taxi-requests/{id}
    public static async Task<IResult> GetRequest(Guid id, ApplicationDbContext db, CancellationToken ct)
    {
        var detail = await LoadDetailAsync(db, id, ct);
        return detail is null ? NotFoundResponse("Taxi request not found") : OkResponse(detail);
    }

    // POST /admin/taxi-requests/{id}/cancel
    public static async Task<IResult> CancelRequest(
        Guid id, AdminCancelTaxiRequestDto? body, TaxiRequestService service, ApplicationDbContext db, CancellationToken ct)
    {
        var result = await service.CancelByAdminAsync(id, body?.Note ?? "", ct);
        if (result.Error is { } error) return Fail(error);
        var detail = await LoadDetailAsync(db, id, ct);
        return detail is null ? NotFoundResponse("Taxi request not found") : OkResponse(detail);
    }

    // GET /admin/taxi-bookings
    public static async Task<IResult> GetBookings(string? state, string? cursor, int? size, ApplicationDbContext db, CancellationToken ct)
    {
        var states = AdminTaxiRequestRules.BookingStatesOf(state);
        if (states is null) return BadRequestResponse("State must be active, ended or all");
        var take = AdminTaxiRequestRules.PageSize(size);
        if (!TryCursor(cursor, out var after)) return BadRequestResponse("Cursor is not valid");

        var rows = await AdminTaxiRequestQueries.BookingPage(AdminTaxiRequestQueries.Bookings(db, states), after, take).ToListAsync(ct);
        var more = rows.Count > take;
        rows = rows.Take(take).ToList();

        var items = await BookingItemsAsync(db, rows, ct);
        return OkResponse(new AdminTaxiBookingListDto
        {
            Items = items,
            NextCursor = more && rows.Count > 0 ? AdminTaxiRequestRules.EncodeCursor(rows[^1].BookedAt, rows[^1].Id) : null,
        });
    }

    // GET /admin/taxi-requests/health
    public static async Task<IResult> GetHealth(ApplicationDbContext db, WorkerHeartbeats heartbeats, CancellationToken ct)
        => OkResponse(await BuildHealthAsync(db, heartbeats, DateTime.UtcNow, ct));

    public static async Task<AdminTaxiHealthDto> BuildHealthAsync(ApplicationDbContext db, WorkerHeartbeats heartbeats, DateTime now, CancellationToken ct)
    {
        var requests = db.Set<TaxiRequest>().AsNoTracking();
        var offers = db.Set<TaxiRequestOffer>().AsNoTracking();
        var today = IstTime.IstMidnightUtc(IstTime.TodayIst(now));
        var hourAgo = now.AddHours(-1);
        var weekAgo = now.AddDays(-AdminTaxiRequestRules.AnswerWindowDays);

        var flag = await db.AppFeatureFlags.AsNoTracking().FirstOrDefaultAsync(f => f.FeatureKey == AppFeatureKeys.TripExecutionEnabled, ct);

        var byStateRows = await requests.Where(r => r.CreatedAt >= today).GroupBy(r => r.Status)
            .Select(g => new { State = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(string s) => byStateRows.FirstOrDefault(x => x.State == s)?.Count ?? 0;

        var stuck = await AdminTaxiRequestQueries.Stuck(db, now).CountAsync(ct);

        var duePending = await AdminTaxiRequestQueries.PendingDueActions(db).FirstOrDefaultAsync(ct);
        var outboxPending = await AdminTaxiRequestQueries.PendingOutbox(db).FirstOrDefaultAsync(ct);

        var ringsLastHour = offers.Where(o => o.OfferedAt >= hourAgo);
        var sent = await ringsLastHour.CountAsync(ct);
        var ackDelays = await ringsLastHour.Where(o => o.PushReceivedAt != null)
            .Select(o => new { o.OfferedAt, Received = o.PushReceivedAt!.Value }).ToListAsync(ct);

        var ready = await AdminTaxiRequestQueries.RingReadyDrivers(db).CountAsync(ct);
        var missing = await AdminTaxiRequestQueries.DriversMissingRings(db).CountAsync(ct);

        var booked = await db.Set<TaxiBooking>().AsNoTracking().CountAsync(b => b.BookedAt >= weekAgo, ct);
        var answered = await AdminTaxiRequestQueries.AnsweredOffers(db, weekAgo).CountAsync(ct);
        var unanswered = await AdminTaxiRequestQueries.UnansweredOffers(db, weekAgo).CountAsync(ct);

        var routesWithoutCity = await db.TaxiRoutes.AsNoTracking().CountAsync(r => r.FromCityKey == null || r.ToCityKey == null, ct);

        int? Seconds(string name) => heartbeats.AgeOf(name) is { } age ? (int)age.TotalSeconds : null;

        return new AdminTaxiHealthDto
        {
            SwitchOn = flag?.IsEnabled ?? false,
            RequestsToday = byStateRows.Sum(x => x.Count),
            ByState = new AdminTaxiByStateDto
            {
                Open = Count(RequestStates.Open),
                Collecting = Count(RequestStates.Collecting),
                Selected = Count(RequestStates.Selected),
                Booked = Count(RequestStates.Booked),
                InProgress = Count(RequestStates.InProgress),
                Completed = Count(RequestStates.Completed),
                Cancelled = Count(RequestStates.Cancelled),
                Expired = Count(RequestStates.Expired),
            },
            StuckRequests = stuck,
            PendingDueActions = duePending?.Count ?? 0,
            OldestDueActionSeconds = duePending is null ? 0 : Math.Max(0, (int)(now - duePending.Oldest).TotalSeconds),
            OutboxPending = outboxPending?.Count ?? 0,
            OldestOutboxSeconds = outboxPending is null ? 0 : Math.Max(0, (int)(now - outboxPending.Oldest).TotalSeconds),
            RingsSentLastHour = sent,
            RingsAckedLastHour = ackDelays.Count,
            AckRatioLastHour = sent == 0 ? null : Math.Round((double)ackDelays.Count / sent, 2),
            AckP95SecondsLastHour = sent == 0 ? null : AdminTaxiRequestRules.Percentile95(
                ackDelays.Select(a => Math.Max(0, (a.Received - a.OfferedAt).TotalSeconds)).ToList()),
            DriversRingReady = ready,
            DriversMissingRings = missing,
            BookedThisWeek = booked,
            OffersAnsweredPct7d = AdminTaxiRequestRules.AnsweredPercent(answered, unanswered),
            RoutesWithoutCity = routesWithoutCity,
            WorkerHeartbeatSeconds = new AdminTaxiHeartbeatsDto
            {
                DueActions = Seconds(DueActionsWorker.HeartbeatName),
                Matcher = Seconds(TaxiMatcherService.HeartbeatName),
                OutboxRelay = Seconds(WorkerNames.OutboxRelay),
            },
        };
    }

    private static async Task<AdminTaxiRequestDetailDto?> LoadDetailAsync(ApplicationDbContext db, Guid id, CancellationToken ct)
    {
        var request = await db.Set<TaxiRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return null;

        var title = await db.Trips.AsNoTracking().Where(t => t.Id == request.TripId).Select(t => t.Title).FirstOrDefaultAsync(ct) ?? "";
        var traveller = await db.Users.AsNoTracking().Where(u => u.Id == request.TravellerUserId)
            .Select(u => new { u.Name, u.PhoneNumber }).FirstOrDefaultAsync(ct);

        var offerRows = await db.Set<TaxiRequestOffer>().AsNoTracking().Where(o => o.RequestId == id).OrderBy(o => o.SeqNo).ToListAsync(ct);
        var listingIds = offerRows.Select(o => o.DriverListingId).Distinct().ToList();
        var drivers = await AdminTaxiRequestQueries.DriverContacts(db, listingIds).ToDictionaryAsync(l => l.Id, ct);
        var offers = offerRows.Select(o =>
        {
            drivers.TryGetValue(o.DriverListingId, out var d);
            return AdminTaxiRequestRules.ToOffer(o, d?.Name, d?.ContactPhone ?? d?.PhoneNumber);
        }).ToList();

        var bookingRow = await db.Set<TaxiBooking>().AsNoTracking().Where(b => b.RequestId == id)
            .OrderBy(b => b.Status == BookingStates.Cancelled ? 1 : 0).ThenByDescending(b => b.BookedAt).FirstOrDefaultAsync(ct);
        var booking = bookingRow is null ? null : (await BookingItemsAsync(db, [bookingRow], ct))[0];

        var due = await db.Set<DueAction>().AsNoTracking().Where(d => d.RequestId == id && OpenDueStates.Contains(d.State))
            .OrderBy(d => d.DueAt).ThenBy(d => d.Id).Take(AdminTaxiRequestRules.MaxDueActions).ToListAsync(ct);

        return AdminTaxiRequestRules.ToDetail(request, title, traveller?.Name, traveller?.PhoneNumber, offers, booking,
            due.Select(AdminTaxiRequestRules.ToDueAction));
    }

    private static async Task<List<AdminTaxiBookingItemDto>> BookingItemsAsync(ApplicationDbContext db, IReadOnlyList<TaxiBooking> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var bookingIds = rows.Select(b => b.Id).ToList();
        var listingIds = rows.Select(b => b.DriverListingId).Distinct().ToList();
        var userIds = rows.Select(b => b.TravellerUserId).Distinct().ToList();
        var stars = await db.Set<TaxiRating>().AsNoTracking().Where(r => bookingIds.Contains(r.BookingId))
            .ToDictionaryAsync(r => r.BookingId, r => (int)r.Stars, ct);
        var driverPhones = await AdminTaxiRequestQueries.DriverContacts(db, listingIds)
            .ToDictionaryAsync(l => l.Id, l => l.ContactPhone ?? l.PhoneNumber, ct);
        var travellerPhones = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.PhoneNumber, ct);

        return rows.Select(b => AdminTaxiRequestRules.ToBooking(b, driverPhones.GetValueOrDefault(b.DriverListingId),
            travellerPhones.GetValueOrDefault(b.TravellerUserId), stars.TryGetValue(b.Id, out var s) ? s : null)).ToList();
    }
}
