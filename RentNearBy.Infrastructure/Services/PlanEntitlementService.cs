using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// The one seam that reads limits: one indexed UserPlans query + cached catalog + cached payment flag.
// Payment ON: the active plan's features, else the default plan's. Payment OFF (free mode): element-wise
// max(default, active plan) with ListingLimit raised to FreeModeListingLimit, and IsPaid = false.
public class PlanEntitlementService(ApplicationDbContext db, IPlanCatalogCache catalog, IPaymentFeatureReader payment)
    : IPlanEntitlementService
{
    internal sealed record ActiveRow(Guid Id, Guid UserId, Guid PlanId, string Source, DateTime StartsAt, DateTime EndsAt);

    public async Task<PlanLimits> GetLimitsAsync(Guid userId, string featureKey)
    {
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var now = DateTime.UtcNow;
        var row = await db.UserPlans.AsNoTracking()
            .Where(u => u.UserId == userId && u.FeatureKey == featureKey
                && u.Status == UserPlanStatuses.Active && u.EndsAt > now)
            .Select(u => new ActiveRow(u.Id, u.UserId, u.PlanId, u.Source, u.StartsAt, u.EndsAt))
            .FirstOrDefaultAsync();
        return Compute(snapshot, flag, featureKey, row);
    }

    public async Task<IReadOnlyDictionary<Guid, PlanLimits>> GetLimitsBatchAsync(
        IReadOnlyCollection<Guid> userIds, string featureKey)
    {
        if (userIds.Count == 0) return new Dictionary<Guid, PlanLimits>();
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var now = DateTime.UtcNow;
        var ids = userIds.Distinct().ToList();
        var rows = await db.UserPlans.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.FeatureKey == featureKey
                && u.Status == UserPlanStatuses.Active && u.EndsAt > now)
            .Select(u => new ActiveRow(u.Id, u.UserId, u.PlanId, u.Source, u.StartsAt, u.EndsAt))
            .ToListAsync();
        var byUser = rows.ToDictionary(r => r.UserId);
        return ids.ToDictionary(id => id, id => Compute(snapshot, flag, featureKey, byUser.GetValueOrDefault(id)));
    }

    public async Task<DateTime> ResolveValidUntilAsync(Guid userId, string featureKey)
        => ResolveValidUntil(await GetLimitsAsync(userId, featureKey));

    public DateTime ResolveValidUntil(PlanLimits limits)
    {
        var free = DateTime.UtcNow.AddDays(Math.Max(1, limits.FreeDays));
        if (limits.EndsAt == null) return free;
        // Free mode never shortens a listing below the free window.
        return limits.PaymentEnabled ? limits.EndsAt.Value : (limits.EndsAt.Value > free ? limits.EndsAt.Value : free);
    }

    public async Task<(int Room, int Plot)> GetPublicListingLimitsAsync()
    {
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        int For(string kind)
        {
            var limit = snapshot.DefaultValues(kind).ListingLimit;
            return flag.Enabled ? limit : Math.Max(limit, flag.FreeModeListingLimit ?? 0);
        }
        return (For(ListingKinds.Room), For(ListingKinds.Plot));
    }

    internal static PlanLimits Compute(PlanCatalogSnapshot snapshot, PaymentFeatureState flag, string featureKey, ActiveRow? row)
    {
        var defaultPlan = snapshot.DefaultPlan(featureKey);
        var defaults = defaultPlan?.Values ?? PlanFeatureValues.Fallback;
        var active = row == null ? null : snapshot.GetPlan(row.PlanId);

        PlanFeatureValues values;
        if (flag.Enabled)
        {
            values = active?.Values ?? defaults;
        }
        else
        {
            values = active != null ? PlanFeatureValues.Max(defaults, active.Values) : defaults;
            values = values with { ListingLimit = Math.Max(values.ListingLimit, flag.FreeModeListingLimit ?? 0) };
        }

        var shown = active ?? defaultPlan;
        return new PlanLimits(
            PlanId: shown?.Id ?? Guid.Empty,
            PlanName: shown?.Name ?? "Free",
            IsDefaultPlan: active == null,
            UserPlanId: active != null ? row!.Id : null,
            IsPaid: flag.Enabled && active != null,
            Source: active != null ? row!.Source : null,
            StartsAt: active != null ? row!.StartsAt : null,
            EndsAt: active != null ? row!.EndsAt : null,
            ListingLimit: Math.Max(0, values.ListingLimit),
            PriorityRank: (short)Math.Clamp(values.Priority, 0, short.MaxValue),
            IsFeatured: values.Featured,
            JobsPosts: Math.Max(0, values.JobsPosts),
            PaymentEnabled: flag.Enabled,
            FreeDays: flag.FreeDays);
    }
}
