using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Owner;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using RentNearBy.Infrastructure.Services.TaxiRequests;

namespace RentNearBy.Infrastructure.Services.Owner.Be3;

// V1 / V2 outcome: Error = an OwnerErrorTypes type (with data), else the listing written (or left as it was).
public sealed record PlanVisibleOutcome(string? Error, object? ErrorData, Guid ListingId, Guid? DistrictId, bool Changed, int SavesUsedToday)
{
    public static PlanVisibleOutcome Fail(string error, object? data = null) => new(error, data, Guid.Empty, null, false, 0);
}

// Contract 2.4 V1 / V2, 4.1, 4.3, AU-9: the owner picks which rows stay visible on his plan. Never deletes; 3 changes per IST day;
// refused while payment is OFF or when nothing is (or would be) hidden. One transaction under the listing lock (one retry on a
// deadlock); a route choice closes the driver's offers no visible route serves before the lock (RoutePausedByPlan).
public sealed class PlanVisibleService(
    ApplicationDbContext db,
    IPlanEntitlementService entitlements,
    TaxiRequestClosure closure,
    IOwnerNotifier notifier,
    TimeProvider clock,
    ILogger<PlanVisibleService> logger)
{
    private TaxiClosureResult? _closed;
    private bool _woken;

    private sealed class LockRow
    {
        public Guid Id { get; set; }
        public Guid? DistrictId { get; set; }
        public DateOnly? PlanVisibleSavesDay { get; set; }
        public short PlanVisibleSavesCount { get; set; }
    }

    private sealed class IdRow
    {
        public Guid Id { get; set; }
    }

    public async Task<PlanVisibleOutcome> SaveStayAsync(Guid userId, IReadOnlyList<Guid> photoIds, IReadOnlyList<Guid> roomIds, CancellationToken ct = default)
    {
        var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Stay);
        if (!limits.PaymentEnabled) return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleNotNeeded);
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);

        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var listing = await LockAsync("\"StayListings\"", userId, c);
            if (listing == null) return PlanVisibleOutcome.Fail(OwnerErrorTypes.ListingNotFound);

            var photos = PlanRows.AsRows(await PlanRows.PhotosAsync(db, listing.Id, c));
            var rooms = PlanRows.AsRows(await PlanRows.RoomsAsync(db, listing.Id, c));
            if (photos.Count <= limits.Photos && rooms.Count <= limits.RoomTypes)
                return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleNotNeeded);
            if (!PlanRowChoice.ValidOwnerChoice(photos.Select(p => p.Id).ToHashSet(), photoIds, limits.Photos)
                || !PlanRowChoice.ValidOwnerChoice(rooms.Select(r => r.Id).ToHashSet(), roomIds, limits.RoomTypes))
                return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleInvalid);

            var photoTargets = PlanRowChoice.OwnerPhotos(photos, photoIds);
            var roomTargets = PlanRowChoice.OwnerIndexed(rooms, roomIds.ToHashSet());
            var photoChanges = PlanRowChoice.Changed(photos, photoTargets);
            var roomChanges = PlanRowChoice.Changed(rooms, roomTargets);
            var used = OwnerContext.CountToday(listing.PlanVisibleSavesDay, listing.PlanVisibleSavesCount, today);
            if (photoChanges.Count == 0 && roomChanges.Count == 0)
                return new PlanVisibleOutcome(null, null, listing.Id, listing.DistrictId, false, used);
            if (used >= OwnerLimits.PlanVisibleSavesPerDay)
                return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleLimit, new DailyLimitRefusalDto(OwnerLimits.PlanVisibleSavesPerDay, used));

            await PlanRows.WritePhotosAsync(db, listing.Id, photoChanges, c);
            if (roomChanges.Count > 0) await PlanRows.WriteRoomsAsync(db, listing.Id, roomTargets, c);
            await CountAsync("\"StayListings\"", listing.Id, today, used + 1, minPrice: true, c);
            await notifier.ClearAsync(NoticeKinds.PlanLimitsChanged, NoticeKinds.NoTrip, [userId], c);
            await tx.CommitAsync(c);
            return new PlanVisibleOutcome(null, null, listing.Id, listing.DistrictId, true, used + 1);
        }, ct, logger);
    }

    public async Task<PlanVisibleOutcome> SaveTaxiAsync(Guid userId, IReadOnlyList<Guid> routeIds, CancellationToken ct = default)
    {
        var limits = await entitlements.GetLimitsAsync(userId, ListingKinds.Taxi);
        if (!limits.PaymentEnabled) return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleNotNeeded);
        var today = IstTime.TodayIst(clock.GetUtcNow().UtcDateTime);
        _closed = null;
        _woken = false;

        return await PgRetry.OnceAsync(db, async c =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(c);
            var taxiId = await LiveIdAsync(userId, c);
            if (taxiId == null) return PlanVisibleOutcome.Fail(OwnerErrorTypes.ListingNotFound);

            // Lock order 4.1: request rows and the driver ring lock (closure) before the listing row.
            var planned = await PlanRows.RoutesAsync(db, taxiId.Value, c);
            if (planned.Count <= limits.Routes) return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleNotNeeded);
            if (!PlanRowChoice.ValidOwnerChoice(planned.Select(r => r.Id).ToHashSet(), routeIds, limits.Routes))
                return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleInvalid);
            var chosen = routeIds.ToHashSet();
            var keep = planned.Where(r => chosen.Contains(r.Id))
                .Select(r => new RouteServeKeys(r.FromCityKey, r.ToCityKey, FromPending: r.FromCityKey == null, ToPending: r.ToCityKey == null))
                .ToList();
            if (planned.Any(r => !r.Hidden && !chosen.Contains(r.Id)))
                _closed = await closure.RoutesChangedAsync(db, taxiId.Value, keep, c, OfferEndReasons.RoutePausedByPlan);

            var listing = await LockAsync("\"TaxiListings\"", userId, c);
            if (listing == null || listing.Id != taxiId) return PlanVisibleOutcome.Fail(OwnerErrorTypes.ListingNotFound);
            var routes = PlanRows.AsRows(await PlanRows.RoutesAsync(db, listing.Id, c));
            if (!PlanRowChoice.ValidOwnerChoice(routes.Select(r => r.Id).ToHashSet(), routeIds, limits.Routes))
                return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleInvalid);

            var targets = PlanRowChoice.OwnerIndexed(routes, chosen);
            var changes = PlanRowChoice.Changed(routes, targets);
            var used = OwnerContext.CountToday(listing.PlanVisibleSavesDay, listing.PlanVisibleSavesCount, today);
            if (changes.Count == 0) return new PlanVisibleOutcome(null, null, listing.Id, listing.DistrictId, false, used);
            if (used >= OwnerLimits.PlanVisibleSavesPerDay)
                return PlanVisibleOutcome.Fail(OwnerErrorTypes.PlanVisibleLimit, new DailyLimitRefusalDto(OwnerLimits.PlanVisibleSavesPerDay, used));

            await PlanRows.WriteRoutesAsync(db, listing.Id, targets, c);
            await CountAsync("\"TaxiListings\"", listing.Id, today, used + 1, minPrice: true, c);
            await notifier.ClearAsync(NoticeKinds.PlanLimitsChanged, NoticeKinds.NoTrip, [userId], c);
            await tx.CommitAsync(c);
            _woken = changes.Any(t => !t.Hidden && routes.Any(r => r.Id == t.Id && r.Hidden));
            return new PlanVisibleOutcome(null, null, listing.Id, listing.DistrictId, true, used + 1);
        }, ct, logger);
    }

    // V1 / V2 answers: visible and hidden arrays (this file lifts the HiddenByPlan filter on purpose), meters, changes left today.
    public async Task<StayPlanVisibleResponse> StayAnswerAsync(Guid stayId, PlanLimits limits, int used, CancellationToken ct)
    {
        var photos = await db.StayPhotos.IgnoreQueryFilters().AsNoTracking().Where(p => p.StayListingId == stayId)
            .OrderBy(p => p.PhotoOrder).ThenBy(p => p.Id).Select(p => new { p.Id, p.PhotoUrl, p.PhotoOrder, p.HiddenByPlan }).ToListAsync(ct);
        var rooms = await db.StayRooms.IgnoreQueryFilters().AsNoTracking().Where(r => r.StayListingId == stayId)
            .OrderBy(r => r.RoomIndex)
            .Select(r => new { Dto = new StayRoomDto
            {
                Id = r.Id, RoomIndex = r.RoomIndex, PricePerNight = r.PricePerNight, MaxGuests = r.MaxGuests,
                RoomName = r.RoomName, Meals = r.Meals, Floor = r.Floor,
            }, r.HiddenByPlan }).ToListAsync(ct);
        var visiblePhotos = photos.Where(p => !p.HiddenByPlan).Select(p => new StayPhotoDto { Id = p.Id, Url = p.PhotoUrl }).ToList();
        var visibleRooms = rooms.Where(r => !r.HiddenByPlan).Select(r => r.Dto).ToList();
        return new StayPlanVisibleResponse
        {
            Photos = visiblePhotos,
            Rooms = visibleRooms,
            HiddenPhotos = photos.Where(p => p.HiddenByPlan).Select(p => new HiddenPhotoDto(p.Id, p.PhotoUrl, p.PhotoOrder)).ToList(),
            HiddenRooms = rooms.Where(r => r.HiddenByPlan).Select(r => r.Dto).ToList(),
            Meters =
            [
                new MeterDto(PlanFeatureKeys.Photos, visiblePhotos.Count, limits.Photos),
                new MeterDto(PlanFeatureKeys.RoomTypes, visibleRooms.Count, limits.RoomTypes),
            ],
            SavesLeftToday = Math.Max(0, OwnerLimits.PlanVisibleSavesPerDay - used),
            PaymentEnabled = limits.PaymentEnabled,
        };
    }

    public async Task<TaxiPlanVisibleResponse> TaxiAnswerAsync(
        ICityResolver resolver, Guid taxiId, PlanLimits limits, int used, CancellationToken ct)
    {
        var visible = await db.TaxiRoutes.AsNoTracking().Where(r => r.TaxiListingId == taxiId).OrderBy(r => r.RouteIndex)
            .Select(TaxiHubReader.RouteDto).ToListAsync(ct);
        var hidden = await TaxiHubReader.HiddenRoutesAsync(db, taxiId, ct);
        await TaxiRouteCityKeys.FillNamesAsync(visible.Concat(hidden), resolver, TimeSpan.Zero);
        return new TaxiPlanVisibleResponse
        {
            Routes = visible,
            HiddenRoutes = hidden,
            Meters = [new MeterDto(PlanFeatureKeys.Routes, visible.Count, limits.Routes)],
            SavesLeftToday = Math.Max(0, OwnerLimits.PlanVisibleSavesPerDay - used),
            PaymentEnabled = limits.PaymentEnabled,
        };
    }

    // After the taxi commit: request hints of the closed offers, matcher wake for routes shown again, pickup lists. Never throws.
    public async Task AfterTaxiCommitAsync(Guid taxiId, bool changed)
    {
        var results = new List<TaxiClosureResult>();
        if (_closed != null) results.Add(_closed);
        if (_woken) results.Add(TaxiRequestClosure.DriverOnline(taxiId));
        _closed = null;
        _woken = false;
        try
        {
            await closure.AfterCommitAsync(TaxiRequestClosure.Merge(results), poolChanged: changed);
            if (changed) notifier.Wake();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "plan.visible.after_commit failed");
        }
    }

    private async Task<LockRow?> LockAsync(string table, Guid userId, CancellationToken ct)
    {
#pragma warning disable EF1002 // table is one of two fixed names; the user id is a parameter.
        var rows = await db.Database.SqlQueryRaw<LockRow>($"""
            SELECT "Id", "DistrictId", "PlanVisibleSavesDay", "PlanVisibleSavesCount" FROM {table}
            WHERE "UserId" = @u AND "IsDeleted" = false LIMIT 1 FOR UPDATE
            """, new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = userId }).ToListAsync(ct);
#pragma warning restore EF1002
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<Guid?> LiveIdAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<IdRow>("""
            SELECT "Id" FROM "TaxiListings" WHERE "UserId" = @u AND "IsDeleted" = false LIMIT 1
            """, new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = userId }).ToListAsync(ct);
        return rows.Count == 0 ? null : rows[0].Id;
    }

    // The daily counter and the cheapest visible row in one statement on the listing row.
    private Task<int> CountAsync(string table, Guid listingId, DateOnly today, int count, bool minPrice, CancellationToken ct)
    {
        var price = !minPrice
            ? string.Empty
            : table == "\"StayListings\""
                ? """, "MinPricePerNight" = (SELECT min(r."PricePerNight") FROM "StayRooms" r WHERE r."StayListingId" = x."Id" AND NOT r."HiddenByPlan")"""
                : """, "MinFare" = (SELECT min(r."Fare") FROM "TaxiRoutes" r WHERE r."TaxiListingId" = x."Id" AND NOT r."HiddenByPlan")""";
#pragma warning disable EF1002 // table is one of two fixed names; the values are parameters.
        return db.Database.ExecuteSqlRawAsync($"""
            UPDATE {table} x SET "PlanVisibleSavesDay" = @d, "PlanVisibleSavesCount" = @c{price}, "UpdatedAt" = now()
            WHERE x."Id" = @l
            """,
            [
                new NpgsqlParameter("d", NpgsqlDbType.Date) { Value = today },
                new NpgsqlParameter("c", NpgsqlDbType.Smallint) { Value = (short)count },
                new NpgsqlParameter("l", NpgsqlDbType.Uuid) { Value = listingId },
            ], ct);
#pragma warning restore EF1002
    }
}
