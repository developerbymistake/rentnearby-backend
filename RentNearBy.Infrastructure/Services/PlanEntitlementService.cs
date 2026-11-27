using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services;

// The one seam that reads limits: one indexed UserPlans query + cached catalog + cached payment flag.
// Payment ON: the active plan's features, else the default plan's. Payment OFF (contract 3.1): Plus numbers for limit keys,
// Free for ranking keys, max with an active plan, ListingLimit raised to FreeModeListingLimit, and IsPaid = false.
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
            .Where(u => u.UserId == userId && u.FeatureKey == featureKey)
            .Where(PlanRowSql.Entitled(now))
            .OrderByDescending(u => u.EndsAt)
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
            .Where(u => ids.Contains(u.UserId) && u.FeatureKey == featureKey)
            .Where(PlanRowSql.Entitled(now))
            .Select(u => new ActiveRow(u.Id, u.UserId, u.PlanId, u.Source, u.StartsAt, u.EndsAt))
            .ToListAsync();
        var byUser = rows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EndsAt).First());
        return ids.ToDictionary(id => id, id => Compute(snapshot, flag, featureKey, byUser.GetValueOrDefault(id)));
    }

    public async Task<PlanLimits> ComputeFromRowAsync(string featureKey, EntitledPlanRow? row)
    {
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var active = row == null ? null : new ActiveRow(row.Id, Guid.Empty, row.PlanId, row.Source, row.StartsAt, row.EndsAt);
        return Compute(snapshot, flag, featureKey, active);
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
            // Payment OFF: Plus numbers for every limit key, Free for every ranking key; only an admin grant raises ranking (D-24).
            var off = PlanFeatureValues.OffValues(snapshot, featureKey);
            values = active != null ? PlanFeatureValues.Effective(false, off, active.Values, row!.Source == UserPlanSources.AdminGrant) : off;
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
            FreeDays: flag.FreeDays,
            Photos: Clamp(PlanFeatureKeys.Photos, values.Photos),
            RoomTypes: Clamp(PlanFeatureKeys.RoomTypes, values.RoomTypes),
            Routes: Clamp(PlanFeatureKeys.Routes, values.Routes),
            RingWeight: Clamp(PlanFeatureKeys.RingWeight, values.RingWeight),
            CalendarEntries: Clamp(PlanFeatureKeys.CalendarEntries, values.CalendarEntries),
            BlockedRanges: Clamp(PlanFeatureKeys.BlockedRanges, values.BlockedRanges),
            UpcomingDays: Clamp(PlanFeatureKeys.UpcomingDays, values.UpcomingDays),
            MonthsAhead: Clamp(PlanFeatureKeys.MonthsAhead, values.MonthsAhead),
            MonthSummary: values.MonthSummary,
            Reminders: values.Reminders,
            SpecialPrices: Clamp(PlanFeatureKeys.SpecialPrices, values.SpecialPrices),
            DemandCount: values.DemandCount,
            StayQuotationsPerMonth: Clamp(PlanFeatureKeys.StayQuotationsPerMonth, values.StayQuotationsPerMonth));
    }

    private static int Clamp(string key, int value) => PlanKeyCatalog.Of(key).Clamp(value);
}
