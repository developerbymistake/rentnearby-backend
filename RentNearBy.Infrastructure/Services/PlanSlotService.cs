using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

// Interpolated parts are table names from the fixed Table() whitelist and catalog-typed values; every
// user-controlled value is a positional parameter.
#pragma warning disable EF1002

namespace RentNearBy.Infrastructure.Services;

// Set-based rank effects of plan changes on Stay and Taxi listings. Bulk statements are raw SQL over a
// fixed table whitelist (never user input) with the state predicate repeated in the UPDATE.
public class PlanSlotService(
    ApplicationDbContext db, IPlanEntitlementService entitlements, IPlanCatalogCache catalog, IPaymentFeatureReader payment)
    : ILiveSlotService
{
    private sealed class DistrictRow
    {
        public Guid? DistrictId { get; set; }
    }

    private static string Table(string kind) => kind switch
    {
        ListingKinds.Stay => "\"StayListings\"",
        ListingKinds.Taxi => "\"TaxiListings\"",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown listing kind"),
    };

    public async Task LockOwnerAsync(Guid userId)
    {
        if (db.Database.CurrentTransaction == null)
            throw new InvalidOperationException("LockOwnerAsync requires an open transaction.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Users\" WHERE \"Id\" = {userId} FOR UPDATE");
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

    public async Task ApplyGrantEffectsAsync(Guid userId, string featureKey)
    {
        var limits = await entitlements.GetLimitsAsync(userId, featureKey);
        await SetRankAsync(userId, featureKey, limits.PriorityRank, limits.IsFeatured);
    }

    public async Task<ExpireEffectsResult> ExpireEffectsAsync(Guid userId, string featureKey)
    {
        var limits = await entitlements.GetLimitsAsync(userId, featureKey);
        var reranked = await SetRankAsync(userId, featureKey, limits.PriorityRank, limits.IsFeatured);
        return new ExpireEffectsResult(0, 0, reranked);
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
}
