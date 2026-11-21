using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Owner.Be3;

public sealed record StayEditDecision(string? Error, Guid OwnerUserId, Guid? OldDistrictId, Guid? NewDistrictId)
{
    public static StayEditDecision Fail(string error) => new(error, Guid.Empty, null, null);
}

// Contract 3.5 / AU-5 / A3: the admin review of a live stay's changed review fields. Approve applies the asked values (slug kept),
// reject keeps the live ones; both store the result for the owner's strip, write the notice and the push in the same transaction
// (stay row lock, level 5) and answer 409 when the change is gone or was replaced (pendingEditAt echo).
public sealed class StayPendingEditService(ApplicationDbContext db, IOwnerNotifier notifier, TimeProvider clock, ILogger<StayPendingEditService> logger)
{
    public const int PageSize = OwnerLimits.PageSize;

    private sealed class PageRow
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? OwnerName { get; set; }
        public DateTime At { get; set; }
        public string Json { get; set; } = string.Empty;
        public Guid RegistrationTypeId { get; set; }
        public string RegistrationTypeName { get; set; } = string.Empty;
        public string? RegistrationOtherName { get; set; }
        public string RegistrationNumber { get; set; } = string.Empty;
        public string? Gstin { get; set; }
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public string? Address { get; set; }
        public Guid? DistrictId { get; set; }
        public string? DistrictName { get; set; }
        public Guid? CityId { get; set; }
        public string? CityName { get; set; }
    }

    // A3 list: keyset on (PendingEditAt, Id) over ix_staylistings_pendingedit, plus the waiting count on the first page.
    public async Task<AdminStayPendingEditPageDto> ListAsync((DateTime At, Guid Id)? after, CancellationToken ct = default)
    {
        var q = db.StayListings.AsNoTracking()
            .Where(s => s.PendingEditJson != null && !s.IsDeleted && s.Status == StayStatuses.Approved);
        var waiting = after == null ? await q.CountAsync(ct) : (int?)null;
        if (after is { } a)
            q = q.Where(s => s.PendingEditAt > a.At || (s.PendingEditAt == a.At && s.Id.CompareTo(a.Id) > 0));
        var rows = await q.OrderBy(s => s.PendingEditAt).ThenBy(s => s.Id).Take(PageSize + 1)
            .Select(s => new PageRow
            {
                Id = s.Id, Name = s.Name, OwnerName = s.User.Name, At = s.PendingEditAt!.Value, Json = s.PendingEditJson!,
                RegistrationTypeId = s.RegistrationTypeId, RegistrationTypeName = s.RegistrationTypeName,
                RegistrationOtherName = s.RegistrationOtherName, RegistrationNumber = s.RegistrationNumber, Gstin = s.Gstin,
                Latitude = s.Latitude, Longitude = s.Longitude, Address = s.Address,
                DistrictId = s.DistrictId, DistrictName = s.District != null ? s.District.Name : null,
                CityId = s.CityId, CityName = s.City != null ? s.City.Name : null,
            })
            .ToListAsync(ct);
        var hasMore = rows.Count > PageSize;
        var page = rows.Take(PageSize).Select(Row).Where(r => r != null).Select(r => r!).ToList();
        var next = hasMore ? KeysetCursor.Encode(rows[PageSize - 1].At, rows[PageSize - 1].Id) : null;
        return new AdminStayPendingEditPageDto(page, next, waiting);
    }

    private static AdminStayPendingEditRowDto? Row(PageRow r)
    {
        var pending = StayPendingEditJson.Read(r.Json);
        if (pending == null) return null;
        var live = new StayReviewValues(r.Name, r.RegistrationTypeId, r.RegistrationTypeName, r.RegistrationOtherName, r.RegistrationNumber,
            r.Gstin, r.Latitude, r.Longitude, r.Address, r.DistrictId, r.DistrictName, r.CityId, r.CityName);
        return new AdminStayPendingEditRowDto(r.Id, r.Name, r.OwnerName, r.At, Fields(live, pending),
            PinMovedMeters(live, pending.Asked), new StayPinDto(r.Latitude, r.Longitude), new StayPinDto(pending.Asked.Latitude, pending.Asked.Longitude));
    }

    // Old and new value of every changed field (the admin sees the registration number; never the owner or the public).
    public static IReadOnlyList<StayPendingFieldDto> Fields(StayReviewValues live, StayPendingEdit pending)
    {
        var a = pending.Asked;
        return pending.Fields.Select(f => f switch
        {
            StayReviewFields.Name => new StayPendingFieldDto(f, live.Name, a.Name),
            StayReviewFields.RegistrationType => new StayPendingFieldDto(f, live.RegistrationTypeName, a.RegistrationTypeName),
            StayReviewFields.RegistrationOtherName => new StayPendingFieldDto(f, live.RegistrationOtherName, a.RegistrationOtherName),
            StayReviewFields.RegistrationNumber => new StayPendingFieldDto(f, live.RegistrationNumber, a.RegistrationNumber),
            StayReviewFields.Gstin => new StayPendingFieldDto(f, live.Gstin, a.Gstin),
            StayReviewFields.Location => new StayPendingFieldDto(f, Pin(live.Latitude, live.Longitude), Pin(a.Latitude, a.Longitude)),
            StayReviewFields.Address => new StayPendingFieldDto(f, live.Address, a.Address),
            StayReviewFields.District => new StayPendingFieldDto(f, live.DistrictName, a.DistrictName),
            StayReviewFields.City => new StayPendingFieldDto(f, live.CityName, a.CityName),
            _ => new StayPendingFieldDto(f, null, null),
        }).ToList();
    }

    public static int? PinMovedMeters(StayReviewValues live, StayReviewValues asked)
        => live.Latitude is { } la && live.Longitude is { } lo && asked.Latitude is { } aa && asked.Longitude is { } ao
           && (la != aa || lo != ao)
            ? (int)Math.Round(GeoMath.HaversineKm((double)la, (double)lo, (double)aa, (double)ao) * 1000)
            : null;

    private static string? Pin(decimal? lat, decimal? lng)
        => lat is { } a && lng is { } b ? FormattableString.Invariant($"{a:0.######}, {b:0.######}") : null;

    public Task<StayEditDecision> ApproveAsync(Guid stayId, DateTime pendingEditAt, CancellationToken ct = default)
        => DecideAsync(stayId, pendingEditAt, null, ct);

    public Task<StayEditDecision> RejectAsync(Guid stayId, DateTime pendingEditAt, string reason, CancellationToken ct = default)
        => DecideAsync(stayId, pendingEditAt, reason.Trim(), ct);

    private async Task<StayEditDecision> DecideAsync(Guid stayId, DateTime pendingEditAt, string? reason, CancellationToken ct)
    {
        var decision = await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            if (!await OwnerLocks.LockStayAsync(db, stayId, c)) return StayEditDecision.Fail(OwnerErrorTypes.StayEditGone);
            var stay = await db.StayListings.FirstAsync(s => s.Id == stayId, c);
            var pending = StayPendingEditJson.Read(stay.PendingEditJson);
            if (pending == null || stay.PendingEditAt is not { } at) return StayEditDecision.Fail(OwnerErrorTypes.StayEditGone);
            if (stay.Status != StayStatuses.Approved)
            {
                stay.PendingEditJson = null;
                stay.PendingEditAt = null;
                await db.SaveChangesAsync(c);
                await tx.CommitAsync(c);
                return StayEditDecision.Fail(OwnerErrorTypes.StayEditGone);
            }
            if (!SameInstant(at, pendingEditAt)) return StayEditDecision.Fail(OwnerErrorTypes.StayEditChanged);

            var oldDistrict = stay.DistrictId;
            var now = clock.GetUtcNow().UtcDateTime;
            if (reason == null) Apply(stay, pending);
            stay.PendingEditResult = reason == null ? StayEditResults.Approved : StayEditResults.Rejected;
            stay.PendingEditRejectReason = reason;
            stay.PendingEditResultAt = now;
            stay.PendingEditJson = null;
            stay.PendingEditAt = null;
            stay.UpdatedAt = now;
            await db.SaveChangesAsync(c);

            var kind = reason == null ? NoticeKinds.StayEditApproved : NoticeKinds.StayEditRejected;
            var other = reason == null ? NoticeKinds.StayEditRejected : NoticeKinds.StayEditApproved;
            await notifier.ClearAsync(other, NoticeKinds.NoTrip, [stay.UserId], c);
            await notifier.StoreAsync([new NoticeUpsert(stay.UserId, kind, NoticeKinds.NoTrip, null, stay.Id)], c);
            var push = reason == null
                ? StayOwnerPushCopy.EditApproved(stay.UserId, stay.Id, at, pending.Fields.Contains(StayReviewFields.Name) ? pending.Asked.Name : null)
                : StayOwnerPushCopy.EditRejected(stay.UserId, stay.Id, at, reason);
            await notifier.PushAsync([push], c);
            await tx.CommitAsync(c);
            return new StayEditDecision(null, stay.UserId, oldDistrict, stay.DistrictId);
        }, ct, logger);
        if (decision.Error == null) notifier.Wake();
        return decision;
    }

    // The asked values of the changed fields only (DistrictId / CityId are the stored ids; the slug is kept).
    public static void Apply(StayListing stay, StayPendingEdit pending)
    {
        var a = pending.Asked;
        foreach (var field in pending.Fields)
        {
            switch (field)
            {
                case StayReviewFields.Name: stay.Name = a.Name; break;
                case StayReviewFields.RegistrationType:
                    stay.RegistrationTypeId = a.RegistrationTypeId;
                    stay.RegistrationTypeName = a.RegistrationTypeName;
                    break;
                case StayReviewFields.RegistrationOtherName: stay.RegistrationOtherName = a.RegistrationOtherName; break;
                case StayReviewFields.RegistrationNumber: stay.RegistrationNumber = a.RegistrationNumber; break;
                case StayReviewFields.Gstin: stay.Gstin = a.Gstin; break;
                case StayReviewFields.Location:
                    stay.Latitude = a.Latitude;
                    stay.Longitude = a.Longitude;
                    break;
                case StayReviewFields.Address: stay.Address = a.Address; break;
                case StayReviewFields.District: stay.DistrictId = a.DistrictId; break;
                case StayReviewFields.City: stay.CityId = a.CityId; break;
            }
        }
    }

    // timestamptz keeps microseconds; the echoed value went through JSON, so compare at that precision.
    public static bool SameInstant(DateTime stored, DateTime echoed)
        => Math.Abs((Utc(stored) - Utc(echoed)).Ticks) < 10;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
