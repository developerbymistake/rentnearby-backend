using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
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
    public async Task DeleteAccountAsync(Guid userId)
    {
        using var tx = await context.Database.BeginTransactionAsync();

        // Owner lock first, same order as every grant/refund/role path (Users, then plans/purchases/listings).
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");

        var user = await context.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PhoneNumber, u.Name, u.CreatedAt })
            .FirstOrDefaultAsync();
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

        var (stayIds, taxiIds) = await listingPurgeService.PurgeUserListingsAsync(userId);
        tripPushes.AddRange(await tripClosure.OnListingsRemovedAsync(ListingKinds.Stay, stayIds));
        tripPushes.AddRange(await tripClosure.OnListingsRemovedAsync(ListingKinds.Taxi, taxiIds));

        await context.Users
            .Where(u => u.Id == userId)
            .ExecuteDeleteAsync();

        await tx.CommitAsync();

        await BustListingCachesAsync(stayDistricts, taxiDistricts);
        await tripClosure.AfterCommitAsync(tripPushes);
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
