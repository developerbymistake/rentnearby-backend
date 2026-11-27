using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Entities.Owner;
using RentNearBy.Core.Entities.TaxiRequests;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Buddy;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;
using RentNearBy.Infrastructure.Data;
using StackExchange.Redis;

namespace RentNearBy.Infrastructure.Services;

public class AccountDeletionService(
    ApplicationDbContext context,
    IListingPurgeService listingPurgeService,
    IPlaySubscriptionRepository playSubscriptions,
    IOptions<PlayBillingOptions> playOptions,
    ITripEnquiryClosure tripClosure,
    IListingCacheInvalidator cacheInvalidator,
    IServiceProvider services)
    : IAccountDeletionService
{
    private bool _committed;

    // A deadlock (40P01) or serialization failure before the commit is retried once in a fresh scope: a new context, closure and
    // participants, so nothing collected by the failed attempt is reused (lock order 4.1, REVIEW_SAFETY B2).
    public async Task<AccountDeletionResult> DeleteAccountAsync(Guid userId)
    {
        try
        {
            return await DeleteOnceAsync(userId);
        }
        catch (Exception ex) when (!_committed && TaxiRequests.PgRetry.IsRetryable(ex))
        {
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            // What the pre-deletion Play sync learned in this request travels to the retry scope (token gone, sync unsure).
            scope.ServiceProvider.GetService<Owner.Quote.SubscriptionDeletionContext>()?.CopyFrom(services.GetService<Owner.Quote.SubscriptionDeletionContext>());
            var fresh = scope.ServiceProvider.GetRequiredService<IAccountDeletionService>();
            return fresh is AccountDeletionService again ? await again.DeleteOnceAsync(userId) : await fresh.DeleteAccountAsync(userId);
        }
    }

    internal async Task<AccountDeletionResult> DeleteOnceAsync(Guid userId)
    {
        using var tx = await context.Database.BeginTransactionAsync();

        // Buddy pair locks come before any row lock (contract 5.1), so a buddy accept/request that already holds a pair
        // lock and waits on the Users row (FK) cannot deadlock with this deletion.
        var participants = services.GetServices<IAccountDeletionParticipant>().OrderBy(p => p.Order).ToList();
        foreach (var participant in participants)
            await participant.LockAsync(userId);

        // Owner lock next, same order as every grant/refund/role path (Users, then plans/purchases/listings).
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");

        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PhoneNumber, u.Name, u.CreatedAt, u.RoleId, u.Username })
            .FirstOrDefaultAsync();

        // Owner rule O17: nothing is deleted while anything is active. Read without row locks; a commitment that
        // lands after this read is closed by the closures below, as before the guard existed.
        var blocks = AccountDeletionRules.Blocks(await ReadActivityAsync(userId)).ToList();
        foreach (var participant in participants)
            blocks.AddRange(await participant.CheckAsync(userId));
        if (blocks.Count > 0)
        {
            await tx.RollbackAsync();
            // An auto-renewing subscription is always the first reason (its one step comes before everything else).
            return new AccountDeletionResult(blocks.OrderBy(b => b.Code == AccountDeletionReasons.ActiveSubscription ? 0 : 1).ToList());
        }

        if (user != null)
        {
            // Latest licence/registration details (deleted listings included) outlive the listing purge below
            // for the same 180-day window; the licence blob is copied as-is, still encrypted.
            var taxi = await context.TaxiListings.AsNoTracking()
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.UpdatedAt)
                .Select(t => new { t.DrivingLicenceNumberEnc, t.DateOfBirth })
                .FirstOrDefaultAsync();
            var stay = await context.StayListings.AsNoTracking()
                .Where(s => s.UserId == userId)
                .OrderByDescending(s => s.UpdatedAt)
                .Select(s => new { s.RegistrationTypeName, s.RegistrationOtherName, s.RegistrationNumber, s.Gstin })
                .FirstOrDefaultAsync();

            context.DeletedAccountRecords.Add(new DeletedAccountRecord
            {
                Id = Guid.NewGuid(),
                OriginalUserId = userId,
                PhoneNumber = user.PhoneNumber,
                Name = user.Name,
                Username = user.Username,
                Role = UserRoles.KeyOf(user.RoleId),
                AccountCreatedAt = user.CreatedAt,
                DeletedAt = DateTime.UtcNow,
                DrivingLicenceNumberEnc = taxi?.DrivingLicenceNumberEnc,
                DateOfBirth = taxi?.DateOfBirth,
                StayRegistrationTypeName = stay?.RegistrationTypeName,
                StayRegistrationOtherName = stay?.RegistrationOtherName,
                StayRegistrationNumber = stay?.RegistrationNumber,
                StayGstin = stay?.Gstin,
            });
            await context.SaveChangesAsync();
        }

        await context.DeviceTokens
            .Where(d => d.UserId == userId)
            .ExecuteDeleteAsync();

        await context.NotificationLogs
            .Where(n => n.UserId == userId)
            .ExecuteDeleteAsync();

        await context.Sessions
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();

        // Plans end with the account; UserPlans then cascade-delete with the user. Purchase rows are
        // financial records: kept, anonymised (UserId null). An order still open is closed so a late
        // capture lands as Paid + RoleMismatch and the reconciler refunds it. CouponRedemptions.UserId
        // is nulled by its SetNull FK.
        var now = DateTime.UtcNow;
        await context.UserPlans
            .Where(u => u.UserId == userId && u.Status == UserPlanStatuses.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Status, UserPlanStatuses.Cancelled)
                .SetProperty(u => u.EndsAt, u => u.StartsAt < now ? now : u.StartsAt.AddSeconds(1))
                .SetProperty(u => u.UpdatedAt, now));

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH closed AS (
                UPDATE "PlanPurchases" SET "Status" = 'Expired', "FailureReason" = 'Account deleted'
                WHERE "UserId" = {userId} AND "Status" IN ('Created', 'Pending')
                RETURNING "Id"
            ),
            released AS (
                UPDATE "CouponRedemptions" r SET "Status" = 'Released', "UpdatedAt" = now()
                FROM closed WHERE r."PurchaseId" = closed."Id" AND r."Status" = 'Reserved'
                RETURNING r."CouponId"
            )
            UPDATE "Coupons" c SET
                "RedemptionCount" = GREATEST(c."RedemptionCount" - 1, 0),
                "Status" = CASE WHEN c."Status" = 'Exhausted' THEN 'Active' ELSE c."Status" END,
                "UpdatedAt" = now()
            FROM released WHERE c."Id" = released."CouponId"
            """);

        await context.PlanPurchases
            .Where(p => p.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.UserId, (Guid?)null)
                .SetProperty(p => p.AnonymisedAt, now));

        // Google subscriptions outlive the account: kept anonymised, Detached, and stopped on Google's side by
        // the reconciler (PlayBilling:RoleChangeAction) so nobody keeps paying for a deleted account.
        await playSubscriptions.AnonymiseForUserAsync(userId, now, playOptions.Value.RoleChangeAction);

        // Trip enquiries: the user's own trips close (owners are told), then enquiries to the user's stay/taxi close.
        var tripPushes = new List<TripClosurePush>(await tripClosure.OnTravellerDeletedAsync(userId));

        var stayDistricts = await context.StayListings.AsNoTracking()
            .Where(s => s.UserId == userId && !s.IsDeleted && s.DistrictId != null)
            .Select(s => s.DistrictId!.Value).Distinct().ToListAsync();
        var taxiDistricts = await context.TaxiListings.AsNoTracking()
            .Where(t => t.UserId == userId && !t.IsDeleted && t.DistrictId != null)
            .Select(t => t.DistrictId!.Value).Distinct().ToListAsync();

        // Other owners' calendar rows of his bookings stay until their own clean-up and read "Traveller removed" (contract 6.2 step 5).
        await context.Set<CalendarEntry>()
            .Where(e => e.TravellerUserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Label, string.Empty)
                .SetProperty(e => e.TravellerUserId, (Guid?)null));

        // FB-22: lock order request -> driver listing. The closure (taxi requests first) runs on the listing ids read here,
        // before the purge touches the listing rows; the purge removes exactly these rows (the user row is locked).
        var stayIds = await context.StayListings.Where(s => s.UserId == userId).Select(s => s.Id).ToListAsync();
        var taxiIds = await context.TaxiListings.Where(t => t.UserId == userId).Select(t => t.Id).ToListAsync();
        tripPushes.AddRange(await tripClosure.OnListingsRemovedAsync(ListingKinds.Taxi, taxiIds));
        tripPushes.AddRange(await tripClosure.OnListingsRemovedAsync(ListingKinds.Stay, stayIds));
        await listingPurgeService.PurgeUserListingsAsync(userId);

        // Kept bookings (no FK to Users) stop carrying the person's name.
        await context.Set<TaxiBooking>()
            .Where(b => b.TravellerUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.TravellerName, AccountDeletionRules.DeletedUserName));
        await context.Set<TaxiBooking>()
            .Where(b => b.DriverUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.DriverName, AccountDeletionRules.DeletedUserName));

        foreach (var participant in participants)
            await participant.OnDeletingAsync(userId);

        await context.Users
            .Where(u => u.Id == userId)
            .ExecuteDeleteAsync();

        await tx.CommitAsync();
        _committed = true;

        foreach (var participant in participants)
            await participant.AfterCommitAsync(userId);

        await BustListingCachesAsync(stayDistricts, taxiDistricts);
        await tripClosure.AfterCommitAsync(tripPushes);
        // Never inline (NFR 4.2): photo files go after the commit, best effort; a failure only orphans files.
        await listingPurgeService.DeleteAllUserPhotosBestEffortAsync(userId);
        return AccountDeletionResult.Done;
    }

    // AU-2: the seven checks of O17 in ONE statement, each branch the oldest item of its kind and side with the count of that kind and
    // side folded in as a window count. Driver-side taxi rows are keyed by listing (deleted listings included).
    internal static readonly string ActivitySql = $"""
        WITH my_taxis AS (SELECT t."Id" FROM "TaxiListings" t WHERE t."UserId" = @user)
        (SELECT '{AccountDeletionReasons.ActiveTaxiBooking}' AS "Code", '{AccountDeletionSides.Traveller}' AS "Side", b."RequestId" AS "Id",
                b."TripId" AS "TripId", false AS "IsLead", NULL::text AS "ListingKind", b."BookedAt" AS "CreatedAt", count(*) OVER ()::int AS "Count"
         FROM "TaxiBookings" b WHERE b."TravellerUserId" = @user AND b."Status" IN ('{BookingStates.Booked}', '{BookingStates.InProgress}')
         ORDER BY b."BookedAt", b."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.ActiveTaxiBooking}', '{AccountDeletionSides.Driver}', b."Id", b."TripId", false, NULL, b."BookedAt", count(*) OVER ()::int
         FROM "TaxiBookings" b WHERE b."DriverListingId" IN (SELECT "Id" FROM my_taxis) AND b."Status" IN ('{BookingStates.Booked}', '{BookingStates.InProgress}')
         ORDER BY b."BookedAt", b."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.ActiveTaxiBooking}', '{AccountDeletionSides.Traveller}', e."Id", e."TripId", true, NULL, e."CreatedAt", count(*) OVER ()::int
         FROM "TripEnquiries" e WHERE e."TravellerUserId" = @user AND e."Kind" = '{TripEnquiryKinds.Taxi}'
           AND e."Status" IN ('{TripEnquiryStatuses.ConfirmedByBoth}', '{TripEnquiryStatuses.BookedByTraveller}') AND (e."CheckOut" IS NULL OR e."CheckOut" >= @today)
         ORDER BY e."CreatedAt", e."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.ActiveTaxiBooking}', '{AccountDeletionSides.Driver}', e."Id", e."TripId", true, NULL, e."CreatedAt", count(*) OVER ()::int
         FROM "TripEnquiries" e WHERE e."OwnerUserId" = @user AND e."Kind" = '{TripEnquiryKinds.Taxi}'
           AND e."Status" IN ('{TripEnquiryStatuses.ConfirmedByBoth}', '{TripEnquiryStatuses.BookedByTraveller}') AND (e."CheckOut" IS NULL OR e."CheckOut" >= @today)
         ORDER BY e."CreatedAt", e."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.ActiveStayBooking}', '{AccountDeletionSides.Host}', e."Id", e."TripId", false, NULL, e."CreatedAt", count(*) OVER ()::int
         FROM "TripEnquiries" e WHERE e."OwnerUserId" = @user AND e."Kind" = '{TripEnquiryKinds.Stay}'
           AND e."Status" IN ('{TripEnquiryStatuses.ConfirmedByBoth}', '{TripEnquiryStatuses.BookedByTraveller}') AND (e."CheckOut" IS NULL OR e."CheckOut" >= @today)
         ORDER BY e."CreatedAt", e."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.ActiveStayBooking}', '{AccountDeletionSides.Traveller}', e."Id", e."TripId", false, NULL, e."CreatedAt", count(*) OVER ()::int
         FROM "TripEnquiries" e WHERE e."TravellerUserId" = @user AND e."Kind" = '{TripEnquiryKinds.Stay}'
           AND e."Status" IN ('{TripEnquiryStatuses.ConfirmedByBoth}', '{TripEnquiryStatuses.BookedByTraveller}') AND (e."CheckOut" IS NULL OR e."CheckOut" >= @today)
         ORDER BY e."CreatedAt", e."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.SelectedTaxiOffer}', '{AccountDeletionSides.Driver}', o."Id", NULL::uuid, false, NULL, o."OfferedAt", count(*) OVER ()::int
         FROM "TaxiRequestOffers" o WHERE o."DriverListingId" IN (SELECT "Id" FROM my_taxis) AND o."Status" = '{OfferStates.Selected}'
         ORDER BY o."OfferedAt", o."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.OpenTaxiRequest}', '{AccountDeletionSides.Traveller}', r."Id", r."TripId", false, NULL, r."CreatedAt", count(*) OVER ()::int
         FROM "TaxiRequests" r WHERE r."TravellerUserId" = @user
           AND r."Status" IN ('{RequestStates.Open}', '{RequestStates.Collecting}', '{RequestStates.Selected}')
         ORDER BY r."CreatedAt", r."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.OpenTaxiRequest}', '{AccountDeletionSides.Traveller}', e."Id", e."TripId", true, NULL, e."CreatedAt", count(*) OVER ()::int
         FROM "TripEnquiries" e WHERE e."TravellerUserId" = @user AND e."Kind" = '{TripEnquiryKinds.Taxi}'
           AND e."Status" IN ('{TripEnquiryStatuses.Asked}', '{TripEnquiryStatuses.FareSent}')
         ORDER BY e."CreatedAt", e."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.OpenStayEnquiry}', '{AccountDeletionSides.Host}', e."Id", e."TripId", false, NULL, e."CreatedAt", count(*) OVER ()::int
         FROM "TripEnquiries" e WHERE e."OwnerUserId" = @user AND e."Kind" = '{TripEnquiryKinds.Stay}'
           AND e."Status" IN ('{TripEnquiryStatuses.Asked}', '{TripEnquiryStatuses.Offered}', '{TripEnquiryStatuses.Countered}')
         ORDER BY e."CreatedAt", e."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.OpenStayEnquiry}', '{AccountDeletionSides.Traveller}', e."Id", e."TripId", false, NULL, e."CreatedAt", count(*) OVER ()::int
         FROM "TripEnquiries" e WHERE e."TravellerUserId" = @user AND e."Kind" = '{TripEnquiryKinds.Stay}'
           AND e."Status" IN ('{TripEnquiryStatuses.Asked}', '{TripEnquiryStatuses.Offered}', '{TripEnquiryStatuses.Countered}')
         ORDER BY e."CreatedAt", e."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.LiveListing}', '{AccountDeletionSides.Driver}', t."Id", NULL::uuid, false, '{ListingKinds.Taxi}', t."CreatedAt", count(*) OVER ()::int
         FROM "TaxiListings" t WHERE t."UserId" = @user AND NOT t."IsDeleted" AND t."Status" = '{TaxiStatuses.Approved}' AND t."IsAvailable"
         ORDER BY t."CreatedAt", t."Id" LIMIT 1)
        UNION ALL
        (SELECT '{AccountDeletionReasons.LiveListing}', '{AccountDeletionSides.Host}', s."Id", NULL::uuid, false, '{ListingKinds.Stay}', s."CreatedAt", count(*) OVER ()::int
         FROM "StayListings" s WHERE s."UserId" = @user AND NOT s."IsDeleted" AND s."Status" = '{StayStatuses.Approved}' AND s."AcceptingGuests"
         ORDER BY s."CreatedAt", s."Id" LIMIT 1)
        """;

    private async Task<IReadOnlyList<AccountActivityItem>> ReadActivityAsync(Guid userId)
    {
        var today = TripExecutionRules.TodayIst(DateTime.UtcNow);
        var rows = await context.Database.SqlQueryRaw<ActivityRow>(ActivitySql,
            new Npgsql.NpgsqlParameter("user", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = userId },
            new Npgsql.NpgsqlParameter("today", NpgsqlTypes.NpgsqlDbType.Date) { Value = today }).ToListAsync();
        return rows.Select(r => new AccountActivityItem(r.Code, r.Side, r.Id, r.Count, r.CreatedAt, r.TripId, r.IsLead, r.ListingKind)).ToList();
    }

    internal sealed class ActivityRow
    {
        public string Code { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public Guid Id { get; set; }
        public Guid? TripId { get; set; }
        public bool IsLead { get; set; }
        public string? ListingKind { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Count { get; set; }
    }

    // Same caches the stay/taxi delete paths clear: the feeds, the trip supply counts and the taxi ways version.
    private async Task BustListingCachesAsync(List<Guid> stayDistricts, List<Guid> taxiDistricts)
    {
        var redis = services.GetService<IConnectionMultiplexer>();
        foreach (var districtId in stayDistricts.Union(taxiDistricts))
            await TripSupplyCache.BumpAsync(redis, districtId);
        if (taxiDistricts.Count > 0) await TaxiWaysVersion.BumpAsync(redis);
        if (stayDistricts.Count > 0) await cacheInvalidator.BustDistrictsAsync(ListingKinds.Stay, stayDistricts);
        if (taxiDistricts.Count > 0) await cacheInvalidator.BustDistrictsAsync(ListingKinds.Taxi, taxiDistricts);
    }
}
