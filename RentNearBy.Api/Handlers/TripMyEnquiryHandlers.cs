using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Traveller My enquiries (stay rows only) and the similar stays of a step.
public static class TripMyEnquiryHandlers
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 50;
    private const int SimilarMax = 3;
    private const int SimilarRateMax = 60;
    private static readonly TimeSpan SimilarRateWindow = TimeSpan.FromMinutes(1);

    // GET /trips/enquiries/mine?bucket=waiting|offers|booked|past&pageSize&cursor&counts
    public static async Task<IResult> GetMine(
        string? bucket, int? pageSize, string? cursor, bool? counts, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();

        var box = string.IsNullOrWhiteSpace(bucket) ? TripMyBuckets.Waiting : bucket.Trim().ToLowerInvariant();
        if (!TripMyBuckets.IsValid(box)) return BadRequestResponse("Bucket must be waiting, offers, booked or past");
        if (cursor != null && !OwnerEnquiryCursor.TryDecode(cursor, out _, out _)) return BadRequestResponse("Invalid cursor");
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var now = DateTime.UtcNow;
        var today = TripExecutionRules.TodayIst(now);

        var waiting = TripMyBuckets.WaitingStatuses;
        var offers = TripMyBuckets.OffersStatuses;
        var booked = TripMyBuckets.BookedStatuses;
        var query = db.TripEnquiries.AsNoTracking().Where(e => e.TravellerUserId == callerId && e.Kind == TripEnquiryKinds.Stay);
        query = box switch
        {
            TripMyBuckets.Waiting => query.Where(e => waiting.Contains(e.Status)),
            TripMyBuckets.Offers => query.Where(e => offers.Contains(e.Status)),
            TripMyBuckets.Booked => query.Where(e => booked.Contains(e.Status) && (e.CheckOut == null || e.CheckOut >= today)),
            _ => query.Where(e => (!waiting.Contains(e.Status) && !offers.Contains(e.Status) && !booked.Contains(e.Status))
                || (booked.Contains(e.Status) && e.CheckOut != null && e.CheckOut < today)),
        };
        if (OwnerEnquiryCursor.TryDecode(cursor, out var afterAt, out var afterId))
            query = query.Where(e => e.CreatedAt < afterAt || (e.CreatedAt == afterAt && e.Id.CompareTo(afterId) < 0));
        var rows = await query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(size + 1).ToListAsync();
        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(size);

        TripMyCountsDto? countsDto = null;
        if (counts != false)
        {
            var groups = await db.TripEnquiries.AsNoTracking()
                .Where(e => e.TravellerUserId == callerId && e.Kind == TripEnquiryKinds.Stay)
                .GroupBy(e => new { e.Status, Ended = e.CheckOut != null && e.CheckOut < today })
                .Select(g => new { g.Key.Status, g.Key.Ended, Count = g.Count() })
                .ToListAsync();
            int Sum(string b) => groups.Where(g => TripMyBuckets.Of(g.Status, g.Ended) == b).Sum(g => g.Count);
            countsDto = new TripMyCountsDto
            {
                Waiting = Sum(TripMyBuckets.Waiting), Offers = Sum(TripMyBuckets.Offers),
                Booked = Sum(TripMyBuckets.Booked), Past = Sum(TripMyBuckets.Past),
            };
        }

        return OkResponse(new TripMyEnquiryPageDto
        {
            Items = await TripEnquiryDtoBuilder.BuildManyAsync(unitOfWork, rows, callerId, now, db),
            HasMore = hasMore,
            NextCursor = hasMore && rows.Count > 0 ? OwnerEnquiryCursor.Encode(rows[^1].CreatedAt, rows[^1].Id) : null,
            Counts = countsDto,
        });
    }

    // GET /trips/enquiries/mine/count: offers = the header badge (offers she has not answered).
    public static async Task<IResult> GetMineCount(ClaimsPrincipal principal, ApplicationDbContext db)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        var today = TripExecutionRules.TodayIst(DateTime.UtcNow);
        var open = TripEnquiryStatuses.Open;
        var groups = await db.TripEnquiries.AsNoTracking()
            .Where(e => e.TravellerUserId == callerId && e.Kind == TripEnquiryKinds.Stay && open.Contains(e.Status))
            .GroupBy(e => new { e.Status, Ended = e.CheckOut != null && e.CheckOut < today })
            .Select(g => new { g.Key.Status, g.Key.Ended, Count = g.Count() })
            .ToListAsync();
        int Sum(string b) => groups.Where(g => TripMyBuckets.Of(g.Status, g.Ended) == b).Sum(g => g.Count);
        return OkResponse(new TripMyOpenCountDto
        {
            Offers = Sum(TripMyBuckets.Offers), Waiting = Sum(TripMyBuckets.Waiting), Booked = Sum(TripMyBuckets.Booked),
        });
    }

    // GET /trips/{tripId}/steps/{stepKey}/similar-stays?like={listingId}&limit=3. A hint only: nothing is ever sent from here.
    public static async Task<IResult> GetSimilarStays(
        Guid tripId, string stepKey, Guid? like, int? limit, ClaimsPrincipal principal, IUnitOfWork unitOfWork, ApplicationDbContext db,
        ICityResolver resolver, IRateLimitService rateLimiter, IServiceProvider sp, HttpContext httpContext)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var callerId)) return UnauthorizedResponse();
        var rl = await rateLimiter.CheckAsync($"trip:similar:{callerId}", SimilarRateMax, SimilarRateWindow);
        if (!rl.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = ((int)Math.Ceiling((rl.RetryAfter ?? SimilarRateWindow).TotalSeconds)).ToString();
            return TooManyRequestsResponse();
        }

        var trip = await unitOfWork.Trips.GetByIdForOwnerAsync(tripId, callerId);
        if (trip == null) return ErrorResponse(404, "Trip not found", TripErrorTypes.TripNotFound);
        var plan = TripPlanReader.Read(trip);
        if (TripSimilarStays.LegOfStep(stepKey) is not { } legIndex || !TripEasyRows.StepKeysOf(plan).Contains(stepKey))
            return BadRequestResponse("Invalid step", TripEnquiryErrorTypes.InvalidStepKey);
        var start = plan.Settings?.StartDate ?? trip.StartDate;
        if (start is not { } startDate)
            return ErrorResponse(422, "Add your trip dates first", TripEnquiryErrorTypes.TripDatesRequired);
        var take = Math.Clamp(limit ?? SimilarMax, 1, SimilarMax);
        var now = DateTime.UtcNow;

        var firstDay = plan.LegStart(legIndex);
        var lastDay = plan.LegEnd(legIndex);
        var leg = plan.Legs is { Count: > 0 } legs && legIndex <= legs.Count ? legs[legIndex - 1] : null;
        var cityKey = leg?.CityKey ?? plan.CityKey;
        var nights = Math.Max(1, lastDay - firstDay + 1);
        var checkIn = TripExecutionRules.NightDate(startDate, Math.Max(1, firstDay));
        var guests = (plan.Settings?.Adults ?? 0) + (plan.Settings?.Kids ?? 0);
        int? party = guests > 0 ? guests : null;

        var stepRows = await db.TripEnquiries.AsNoTracking()
            .Where(e => e.TripId == tripId && e.StepKey == stepKey)
            .ToListAsync();
        var shortlist = await db.TripShortlistItems.AsNoTracking()
            .Where(i => i.TripId == tripId && i.StepKey == stepKey
                && (i.State == TripShortlistStates.Active || i.State == TripShortlistStates.Sent))
            .Select(i => new { i.ListingId, i.State })
            .ToListAsync();
        var openRows = stepRows.Where(e => TripEnquiryStatuses.IsOpen(e.Status)).ToList();
        var free = TripStepSlotRules.Compute(openRows, shortlist.Count(i => i.State == TripShortlistStates.Active), now).Free;

        var referenceId = like ?? stepRows.Where(e => e.Kind == TripEnquiryKinds.Stay).MaxBy(e => e.CreatedAt)?.ListingId;
        var referenceRow = referenceId is { } rid ? stepRows.Where(e => e.ListingId == rid).MaxBy(e => e.CreatedAt) : null;

        var city = await StayHandlers.CityStayItemsAsync(cityKey, leg?.Latitude, leg?.Longitude, null, null, unitOfWork, resolver, sp,
            party, null, null, nights, checkIn);
        var candidates = city.Items;

        var refCandidate = referenceId is { } r1 ? candidates.FirstOrDefault(c => c.Id == r1) : null;
        int? refPrice = refCandidate != null
            ? TripSimilarStays.PricePerNight(refCandidate)
            : referenceRow?.ListedStayTotal is { } total ? total / Math.Max(1, TripExecutionRules.Nights(referenceRow.CheckIn, referenceRow.CheckOut)) : null;
        var refType = refCandidate?.StayTypeId;
        if (refCandidate == null && referenceId is { } r2)
            refType = await db.StayListings.AsNoTracking().Where(s => s.Id == r2).Select(s => s.StayTypeId).FirstOrDefaultAsync();
        (double Lat, double Lng)? refPoint = refCandidate != null
            ? ((double)refCandidate.Latitude, (double)refCandidate.Longitude)
            : referenceRow is { ListingLatitude: { } la, ListingLongitude: { } lo } ? (la, lo) : null;

        var excluded = stepRows.Select(e => e.ListingId).Concat(shortlist.Select(i => i.ListingId)).ToHashSet();
        if (referenceId is { } r3) excluded.Add(r3);
        var ids = candidates.Select(c => c.Id).ToList();
        var owners = ids.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await db.StayListings.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.UserId);
        var blocked = owners.Count == 0
            ? new HashSet<Guid>()
            : await TripBulk.BlockedOwnersAsync(db, callerId, owners.Values.Where(o => o != callerId).Distinct().ToList());
        foreach (var (stayId, ownerId) in owners)
            if (ownerId == callerId || blocked.Contains(ownerId)) excluded.Add(stayId);

        var items = TripSimilarStays.Pick(candidates, refType, refPrice, refPoint, excluded, take);
        foreach (var s in items) { s.Rules = null; s.Blocks = null; }
        return OkResponse(new SimilarStaysDto
        {
            Free = free,
            Reference = new SimilarStaysReferenceDto { ListingId = referenceId, PricePerNight = refPrice, StayTypeId = refType },
            Items = StayGeoRefine.WithMapThumbs(items),
        });
    }
}

// Pure part of similar stays: same type, price within 30 percent, fits, free on the dates; fast repliers first.
public static class TripSimilarStays
{
    public static int? LegOfStep(string? stepKey)
    {
        if (!TripStepKeys.IsValid(stepKey) || !stepKey!.StartsWith("stay:", StringComparison.Ordinal)) return null;
        return int.TryParse(stepKey.AsSpan(5), out var k) && k >= 1 ? k : null;
    }

    public static int? PricePerNight(StayNearbyDto s) => s.DatesPerNight ?? s.QuotePerNight ?? s.MinPricePerNight;

    public static bool PriceClose(int? price, int? reference)
        => price is not { } p || reference is not { } r || (long)Math.Abs(p - r) * 10 <= (long)r * 3;

    public static List<StayNearbyDto> Pick(
        IReadOnlyList<StayNearbyDto> candidates, Guid? refType, int? refPrice, (double Lat, double Lng)? refPoint,
        IReadOnlySet<Guid> excluded, int take)
    {
        double Distance(StayNearbyDto s) => refPoint is { } p ? GeoMath.HaversineKm(p.Lat, p.Lng, (double)s.Latitude, (double)s.Longitude) : 0;
        long PriceGap(StayNearbyDto s) => PricePerNight(s) is { } a && refPrice is { } b ? Math.Abs((long)a - b) : long.MaxValue;
        return candidates
            .Where(s => !excluded.Contains(s.Id))
            .Where(s => refType == null || s.StayTypeId == refType)
            .Where(s => PriceClose(PricePerNight(s), refPrice))
            .Where(s => s.Fits != false && s.FullOnDates != true)
            .OrderByDescending(s => s.RepliesFast == true)
            .ThenByDescending(s => s.PriorityRank)
            .ThenBy(PriceGap)
            .ThenBy(Distance)
            .ThenBy(s => s.Id)
            .Take(take)
            .ToList();
    }
}
