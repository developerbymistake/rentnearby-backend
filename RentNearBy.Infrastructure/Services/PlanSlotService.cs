using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

// Interpolated parts are table names from the fixed Table() whitelist and catalog-typed values; every
// user-controlled value is a positional parameter.
#pragma warning disable EF1002

namespace RentNearBy.Infrastructure.Services;

// Room/plot slot counting and the set-based listing effects of plan changes. Bulk statements are raw
// SQL over a fixed table whitelist (never user input) with the state predicate repeated in the UPDATE.
public class PlanSlotService(
    ApplicationDbContext db, IPlanEntitlementService entitlements, IPlanCatalogCache catalog, IPaymentFeatureReader payment,
    ILogger<PlanSlotService> logger)
    : ILiveSlotService
{
    private sealed class DistrictRow
    {
        public Guid? DistrictId { get; set; }
    }

    private static string Table(string kind) => kind switch
    {
        ListingKinds.Room => "\"RoomListings\"",
        ListingKinds.Plot => "\"PlotListings\"",
        ListingKinds.Stay => "\"StayListings\"",
        ListingKinds.Taxi => "\"TaxiListings\"",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown listing kind"),
    };

    private static string SlotTable(string kind) => PlanFeatures.HasListingSlots(kind)
        ? Table(kind)
        : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only rooms and plots have listing slots");

    public async Task LockOwnerAsync(Guid userId)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("LockOwnerAsync requires an open transaction.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");
    }

    public async Task<int> CountForCreationAsync(Guid userId, string kind) => kind switch
    {
        ListingKinds.Room => await db.RoomListings.AsNoTracking()
            .CountAsync(l => l.UserId == userId && !l.IsDeleted && l.LiveRequestStatus != GoLiveRequestStatuses.Rejected),
        ListingKinds.Plot => await db.PlotListings.AsNoTracking()
            .CountAsync(l => l.UserId == userId && !l.IsDeleted && l.LiveRequestStatus != GoLiveRequestStatuses.Rejected),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only rooms and plots have listing slots"),
    };

    public async Task<int> CountLiveAsync(Guid userId, string kind, Guid? excludeId = null)
    {
        var except = excludeId ?? Guid.Empty;
        return kind switch
        {
            ListingKinds.Room => await db.RoomListings.AsNoTracking()
                .CountAsync(l => l.UserId == userId && !l.IsDeleted && l.Id != except
                    && (l.IsActive || l.LiveRequestStatus == GoLiveRequestStatuses.Pending)),
            ListingKinds.Plot => await db.PlotListings.AsNoTracking()
                .CountAsync(l => l.UserId == userId && !l.IsDeleted && l.Id != except
                    && (l.IsActive || l.LiveRequestStatus == GoLiveRequestStatuses.Pending)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only rooms and plots have listing slots"),
        };
    }

    public async Task<int> CountPlanPausedAsync(Guid userId, string kind) => kind switch
    {
        ListingKinds.Room => await db.RoomListings.AsNoTracking()
            .CountAsync(l => l.UserId == userId && !l.IsDeleted && !l.IsActive && l.PauseReason == PauseReasons.Plan),
        ListingKinds.Plot => await db.PlotListings.AsNoTracking()
            .CountAsync(l => l.UserId == userId && !l.IsDeleted && !l.IsActive && l.PauseReason == PauseReasons.Plan),
        _ => 0,
    };

    public async Task<int> ResumePlanPausedAsync(Guid userId, string kind, int slots, DateTime validUntil)
    {
        if (slots <= 0) return 0;
        var t = SlotTable(kind);
        // A suspended owner never gets rows back; UnsuspendAsync re-applies the slots on restore.
        return await db.Database.ExecuteSqlRawAsync($"""
            UPDATE {t} SET "IsActive" = true, "PauseReason" = NULL, "ValidUntil" = {"{1}"}, "UpdatedAt" = now()
            WHERE "Id" IN (
                SELECT "Id" FROM {t}
                WHERE "UserId" = {"{0}"} AND "IsDeleted" = false AND "IsActive" = false AND "PauseReason" = 'Plan'
                ORDER BY "CreatedAt" DESC, "Id" DESC
                LIMIT {"{2}"})
              AND "IsActive" = false AND "PauseReason" = 'Plan'
              AND EXISTS (SELECT 1 FROM "Users" u WHERE u."Id" = {"{0}"} AND u."IsActive" = true)
            """, userId, validUntil, slots);
    }

    public async Task<int> PlanPauseExtraAsync(Guid userId, string kind, int keep)
    {
        var t = SlotTable(kind);
        return await db.Database.ExecuteSqlRawAsync($"""
            UPDATE {t} SET "IsActive" = false, "PauseReason" = 'Plan', "UpdatedAt" = now()
            WHERE "UserId" = {"{0}"} AND "IsDeleted" = false AND "IsActive" = true
              AND "Id" NOT IN (
                SELECT "Id" FROM {t}
                WHERE "UserId" = {"{0}"} AND "IsDeleted" = false
                  AND "IsActive" = true
                ORDER BY "CreatedAt" DESC, "Id" DESC
                LIMIT {"{1}"})
            """, userId, Math.Max(0, keep));
    }

    public async Task<int> SetRankAsync(Guid userId, string kind, short rank, bool featured)
    {
        var t = Table(kind);
        return await db.Database.ExecuteSqlRawAsync($"""
            UPDATE {t} SET "PriorityRank" = {"{1}"}, "IsFeatured" = {"{2}"}
            WHERE "UserId" = {"{0}"} AND "IsDeleted" = false
              AND ("PriorityRank" <> {"{1}"} OR "IsFeatured" <> {"{2}"})
            """, userId, rank, featured);
    }

    public async Task<int> ExtendValidUntilAsync(Guid userId, string kind, DateTime validUntil)
    {
        var t = SlotTable(kind);
        return await db.Database.ExecuteSqlRawAsync($"""
            UPDATE {t} SET "ValidUntil" = {"{1}"}
            WHERE "UserId" = {"{0}"} AND "IsDeleted" = false AND "IsActive" = true
            """, userId, validUntil);
    }

    public async Task ApplyGrantEffectsAsync(Guid userId, string featureKey)
    {
        var limits = await entitlements.GetLimitsAsync(userId, featureKey);
        if (PlanFeatures.HasListingSlots(featureKey))
        {
            var validUntil = entitlements.ResolveValidUntil(limits);
            var live = await CountLiveAsync(userId, featureKey);
            if (live > limits.ListingLimit)
                await PlanPauseExtraAsync(userId, featureKey, limits.ListingLimit);
            else if (live < limits.ListingLimit)
                await ResumePlanPausedAsync(userId, featureKey, limits.ListingLimit - live, validUntil);
            await ExtendValidUntilAsync(userId, featureKey, validUntil);
        }
        await SetRankAsync(userId, featureKey, limits.PriorityRank, limits.IsFeatured);
    }

    public async Task<ExpireEffectsResult> ExpireEffectsAsync(Guid userId, string featureKey)
    {
        var limits = await entitlements.GetLimitsAsync(userId, featureKey);
        int paused = 0, extended = 0;
        if (PlanFeatures.HasListingSlots(featureKey))
        {
            // In free mode ListingLimit is already max(default, FreeModeListingLimit), so this never pauses below it.
            paused = await PlanPauseExtraAsync(userId, featureKey, limits.ListingLimit);

            // Kept rows fall back to the free window (also pulls a refunded/revoked plan's long ValidUntil in).
            var t = SlotTable(featureKey);
            var freeUntil = entitlements.ResolveValidUntil(limits);
            extended = await db.Database.ExecuteSqlRawAsync($"""
                UPDATE {t} SET "ValidUntil" = {"{1}"}
                WHERE "UserId" = {"{0}"} AND "IsDeleted" = false AND "IsActive" = true
                  AND ("ValidUntil" IS NULL OR "ValidUntil" <= now() OR "ValidUntil" > {"{1}"})
                """, userId, freeUntil);
        }
        var reranked = await SetRankAsync(userId, featureKey, limits.PriorityRank, limits.IsFeatured);
        return new ExpireEffectsResult(paused, extended, reranked);
    }

    public async Task<int> ResumeAfterSlotFreedAsync(Guid userId, string kind)
    {
        var limits = await entitlements.GetLimitsAsync(userId, kind);
        var live = await CountLiveAsync(userId, kind);
        if (live >= limits.ListingLimit) return 0;
        return await ResumePlanPausedAsync(userId, kind, limits.ListingLimit - live, entitlements.ResolveValidUntil(limits));
    }

    public async Task<int> SuspendAsync(Guid userId)
    {
        var total = 0;
        foreach (var kind in new[] { ListingKinds.Room, ListingKinds.Plot })
        {
            var t = SlotTable(kind);
            total += await db.Database.ExecuteSqlRawAsync($"""
                UPDATE {t} SET "IsActive" = false, "PauseReason" = 'Suspended', "UpdatedAt" = now()
                WHERE "UserId" = {"{0}"} AND "IsDeleted" = false AND "IsActive" = true
                """, userId);
        }
        return total;
    }

    // Suspended rows are revived first, newest first, up to the free slots; the rest become plan-paused, then
    // any slot still free resumes the newest plan-paused rows. Needs Users.IsActive = true in this transaction.
    public async Task<int> UnsuspendAsync(Guid userId)
    {
        var revived = 0;
        foreach (var kind in new[] { ListingKinds.Room, ListingKinds.Plot })
        {
            var t = SlotTable(kind);
            var limits = await entitlements.GetLimitsAsync(userId, kind);
            var live = await CountLiveAsync(userId, kind);
            var slots = Math.Max(0, limits.ListingLimit - live);
            var validUntil = entitlements.ResolveValidUntil(limits);
            if (slots > 0)
            {
                revived += await db.Database.ExecuteSqlRawAsync($"""
                    UPDATE {t} SET "IsActive" = true, "PauseReason" = NULL, "UpdatedAt" = now(),
                        "ValidUntil" = CASE WHEN "ValidUntil" IS NULL OR "ValidUntil" <= now() THEN {"{1}"} ELSE "ValidUntil" END
                    WHERE "Id" IN (
                        SELECT "Id" FROM {t}
                        WHERE "UserId" = {"{0}"} AND "IsDeleted" = false AND "IsActive" = false AND "PauseReason" = 'Suspended'
                        ORDER BY "CreatedAt" DESC, "Id" DESC
                        LIMIT {"{2}"})
                      AND "IsActive" = false AND "PauseReason" = 'Suspended'
                    """, userId, validUntil, slots);
            }
            await db.Database.ExecuteSqlRawAsync($"""
                UPDATE {t} SET "PauseReason" = 'Plan', "UpdatedAt" = now()
                WHERE "UserId" = {"{0}"} AND "IsDeleted" = false AND "IsActive" = false AND "PauseReason" = 'Suspended'
                """, userId);
            revived += await ResumeAfterSlotFreedAsync(userId, kind);
        }
        return revived;
    }

    // One set-based UPDATE per kind: rows whose rank/featured differ from the owner's current plan.
    // Plan values are inlined from the cached catalog (typed Guids/ints only, never user input).
    public async Task<RankReconcileResult> ReconcileRanksAsync(string kind)
    {
        var t = Table(kind);
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var defaults = snapshot.DefaultValues(kind);

        var values = new StringBuilder();
        values.Append("('00000000-0000-0000-0000-000000000000'::uuid, 0::smallint, false)");
        foreach (var plan in snapshot.PlansOf(kind))
        {
            var v = flag.Enabled ? plan.Values : PlanFeatureValues.Max(defaults, plan.Values);
            values.Append(CultureInfo.InvariantCulture, $", ('{plan.Id}'::uuid, {Math.Clamp(v.Priority, 0, short.MaxValue)}::smallint, {(v.Featured ? "true" : "false")})");
        }
        var defRank = Math.Clamp(defaults.Priority, 0, short.MaxValue).ToString(CultureInfo.InvariantCulture);
        var defFeatured = defaults.Featured ? "true" : "false";

        var rows = await db.Database.SqlQueryRaw<DistrictRow>($"""
            WITH pv ("PlanId", "Rank", "Featured") AS (VALUES {values}),
            target AS (
                SELECT l."Id",
                       COALESCE(pv."Rank", {defRank}::smallint) AS "Rank",
                       COALESCE(pv."Featured", {defFeatured}) AS "Featured"
                FROM {t} l
                LEFT JOIN "UserPlans" up ON up."UserId" = l."UserId" AND up."FeatureKey" = {"{0}"}
                    AND up."Status" = 'Active' AND up."EndsAt" > now()
                LEFT JOIN pv ON pv."PlanId" = up."PlanId"
                WHERE l."IsDeleted" = false
            )
            UPDATE {t} AS x SET "PriorityRank" = target."Rank", "IsFeatured" = target."Featured"
            FROM target
            WHERE x."Id" = target."Id" AND (x."PriorityRank" <> target."Rank" OR x."IsFeatured" <> target."Featured")
            RETURNING x."DistrictId"
            """, kind).ToListAsync();

        return new RankReconcileResult(
            rows.Count,
            rows.Where(r => r.DistrictId != null).Select(r => r.DistrictId!.Value).Distinct().ToList());
    }

    public async Task<List<Guid>> ListOverLimitOwnersAsync(string kind, int limit)
    {
        var t = SlotTable(kind);
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var defaults = snapshot.DefaultValues(kind);
        var freeMode = flag.FreeModeListingLimit ?? 0;

        // Mirrors PlanEntitlementService.Compute; GetLimitsAsync stays authoritative under the owner lock.
        var defLimit = Math.Max(0, flag.Enabled ? defaults.ListingLimit : Math.Max(defaults.ListingLimit, freeMode));
        var minLimit = defLimit;
        var values = new StringBuilder();
        values.Append("('00000000-0000-0000-0000-000000000000'::uuid, 0)");
        foreach (var plan in snapshot.PlansOf(kind))
        {
            var planLimit = Math.Max(0, flag.Enabled
                ? plan.Values.ListingLimit
                : Math.Max(Math.Max(defaults.ListingLimit, plan.Values.ListingLimit), freeMode));
            minLimit = Math.Min(minLimit, planLimit);
            values.Append(CultureInfo.InvariantCulture, $", ('{plan.Id}'::uuid, {planLimit})");
        }
        var def = defLimit.ToString(CultureInfo.InvariantCulture);
        var min = minLimit.ToString(CultureInfo.InvariantCulture);

        return await db.Database.SqlQueryRaw<Guid>($"""
            WITH pv ("PlanId", "Limit") AS (VALUES {values}),
            live AS (
                SELECT l."UserId", COUNT(*) AS "N"
                FROM {t} l
                WHERE l."IsDeleted" = false AND l."IsActive" = true
                GROUP BY l."UserId"
                HAVING COUNT(*) > {min})
            SELECT live."UserId" AS "Value"
            FROM live
            LEFT JOIN "UserPlans" up ON up."UserId" = live."UserId" AND up."FeatureKey" = {"{0}"}
                AND up."Status" = 'Active' AND up."EndsAt" > now()
            LEFT JOIN pv ON pv."PlanId" = up."PlanId"
            WHERE live."N" > COALESCE(pv."Limit", {def}::int)
            ORDER BY live."UserId"
            LIMIT {"{1}"}
            """, kind, limit).ToListAsync();
    }

    public async Task<int> EnforceLimitAsync(Guid userId, string kind)
    {
        var limits = await entitlements.GetLimitsAsync(userId, kind);
        return await PlanPauseExtraAsync(userId, kind, limits.ListingLimit);
    }

    public async Task<(int Paused, IReadOnlyList<Guid> Owners)> EnforceLimitsAsync(string kind)
    {
        if (!PlanFeatures.HasListingSlots(kind)) return (0, []);
        if (db.Database.CurrentTransaction != null)
            throw new InvalidOperationException("EnforceLimitsAsync opens its own transactions.");

        const int Batch = 500;
        const int MaxBatches = 20;
        var done = new HashSet<Guid>();
        var touched = new List<Guid>();
        var paused = 0;
        for (var i = 0; i < MaxBatches; i++)
        {
            var found = await ListOverLimitOwnersAsync(kind, Batch);
            var ids = found.Where(done.Add).ToList();
            if (ids.Count == 0) break;

            foreach (var id in ids)
            {
                try
                {
                    await using var tx = await db.Database.BeginTransactionAsync();
                    await LockOwnerAsync(id);
                    var n = await EnforceLimitAsync(id, kind);
                    await tx.CommitAsync();
                    if (n > 0)
                    {
                        paused += n;
                        touched.Add(id);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Limit enforcement failed for {UserId} ({Kind})", id, kind);
                }
            }
            if (found.Count < Batch) break;
        }
        return (paused, touched);
    }
}
