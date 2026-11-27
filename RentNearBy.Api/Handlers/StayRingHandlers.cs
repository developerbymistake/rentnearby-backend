using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.DTOs.StayRing;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.StayRing;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.StayRing;
using static RentNearBy.Api.Extensions.ApiResults;

namespace RentNearBy.Api.Handlers;

// Stay enquiry ring routes (contract F:/Dhasu/Buddy_resume/stay_ring/CONTRACT.md section 3), policy CanListStays. None of them reads an
// enquiry for writing or changes one: the ring only brings the owner to the enquiry (Accept) or stops (Decline).
public static class StayRingHandlers
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public static string TestRateKey(Guid userId) => $"stay:testring:{userId}";
    public static string AnswerRateKey(Guid userId) => $"stay:ringanswer:{userId}";

    // GET /stays/me/enquiry-ring
    public static async Task<IResult> GetSettings(ClaimsPrincipal principal, ApplicationDbContext db, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var dto = await LoadAsync(db, userId, ct);
        return dto is null ? StayMissing() : OkResponse(dto);
    }

    // PUT /stays/me/enquiry-ring { ringOn }: the server reads the switch before every ring (default on).
    public static async Task<IResult> PutSettings(UpdateStayRingRequest request, ClaimsPrincipal principal, ApplicationDbContext db, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (request?.RingOn is not { } ringOn)
            return BadRequestResponse(StayRingErrorTypes.RingOnRequiredMessage, StayRingErrorTypes.RingOnRequired);
        var changed = await db.StayListings.Where(s => s.UserId == userId && !s.IsDeleted)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EnquiryRingOn, ringOn), ct);
        if (changed == 0) return StayMissing();
        return OkResponse((await LoadAsync(db, userId, ct))!);
    }

    // POST /stays/me/enquiry-ring/test: one test ring to the caller's devices (readiness checklist), 6 per hour like the taxi D12.
    public static async Task<IResult> TestRing(ClaimsPrincipal principal, HttpContext http, ApplicationDbContext db, IRateLimitService rateLimiter,
        IOutboxSignal outbox, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        if (await LimitedAsync(rateLimiter, http, TestRateKey(userId), StayRingRules.TestRingsPerHour) is { } limited) return limited;
        db.OutboxMessages.Add(StayRingTestRing.Outbox(userId, DateTime.UtcNow));
        await db.SaveChangesAsync(ct);
        outbox.Wake();
        return NoContentResponse();
    }

    // POST /stays/me/enquiry-ring/{enquiryId}/answer { outcome, deviceToken }: Accept or Decline on the ring. Cancels the missed ring and
    // stops the ring on the owner's other devices. The enquiry is untouched (use cases 5 and 6).
    public static async Task<IResult> Answer(Guid enquiryId, StayRingAnswerRequest request, ClaimsPrincipal principal, HttpContext http,
        ApplicationDbContext db, IRateLimitService rateLimiter, IOutboxSignal outbox, CancellationToken ct)
    {
        if (!UsersHandlers.TryGetUserId(principal, out var userId)) return UnauthorizedResponse();
        var outcome = request?.Outcome?.Trim();
        if (!StayRingEndReasons.IsOutcome(outcome))
            return BadRequestResponse(StayRingErrorTypes.InvalidOutcomeMessage, StayRingErrorTypes.InvalidOutcome);
        if (await LimitedAsync(rateLimiter, http, AnswerRateKey(userId), StayRingRules.AnswersPerHour) is { } limited) return limited;

        var owner = await db.TripEnquiries.AsNoTracking().Where(e => e.Id == enquiryId && e.Kind == TripEnquiryKinds.Stay)
            .Select(e => new { e.OwnerUserId }).FirstOrDefaultAsync(ct);
        if (owner is null) return ErrorResponse(404, StayRingErrorTypes.EnquiryNotFoundMessage, StayRingErrorTypes.EnquiryNotFound);
        if (owner.OwnerUserId != userId) return ErrorResponse(403, StayRingErrorTypes.NotEnquiryOwnerMessage, StayRingErrorTypes.NotEnquiryOwner);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await StayRingWriter.AnswerAsync(db, enquiryId, userId, outcome!, RingEndReasons.AnsweringToken(request!.DeviceToken), DateTime.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        outbox.Wake();
        return NoContentResponse();
    }

    public static StayRingSettingsDto Build(StayListing? stay, bool hasValidToken)
    {
        var live = stay is { IsDeleted: false, Status: StayStatuses.Approved };
        var ringOn = stay?.EnquiryRingOn ?? true;
        return new StayRingSettingsDto
        {
            RingOn = ringOn,
            StayLive = live,
            HasValidToken = hasValidToken,
            CanRing = live && ringOn && hasValidToken,
            RingSeconds = StayRingRules.RingSeconds,
        };
    }

    private static async Task<StayRingSettingsDto?> LoadAsync(ApplicationDbContext db, Guid userId, CancellationToken ct)
    {
        var stay = await db.StayListings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId && !s.IsDeleted, ct);
        if (stay is null) return null;
        var hasToken = await db.DeviceTokens.AsNoTracking().AnyAsync(t => t.UserId == userId && t.IsValid, ct);
        return Build(stay, hasToken);
    }

    private static IResult StayMissing() => ErrorResponse(404, StayRingErrorTypes.StayNotFoundMessage, StayRingErrorTypes.StayNotFound);

    private static async Task<IResult?> LimitedAsync(IRateLimitService rateLimiter, HttpContext http, string key, int perHour)
    {
        var limit = await rateLimiter.CheckAsync(key, perHour, Hour);
        if (limit.IsAllowed) return null;
        http.Response.Headers["Retry-After"] = ((int)Math.Ceiling((limit.RetryAfter ?? Hour).TotalSeconds)).ToString();
        return TooManyRequestsResponse();
    }
}
