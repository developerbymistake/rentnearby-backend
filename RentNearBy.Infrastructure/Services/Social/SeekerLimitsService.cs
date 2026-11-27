using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Interfaces.Social;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Data;

namespace RentNearBy.Infrastructure.Services.Social;

// Contract 4: the seeker's numbers from the Seeker plans of the catalog snapshot (two indexed reads).
public sealed class SeekerLimitsService(ApplicationDbContext db, IPlanCatalogCache catalog, IPaymentFeatureReader payment, TimeProvider clock) : ISeekerLimits
{
    public Task<SeekerLimits> GetAsync(Guid userId, CancellationToken ct = default) => ReadAsync(userId, withHold: true, ct);

    // A social hold must never make trips inactive: the plan reconcile reads the plan numbers only.
    public Task<SeekerLimits> GetPlanAsync(Guid userId, CancellationToken ct = default) => ReadAsync(userId, withHold: false, ct);

    private async Task<SeekerLimits> ReadAsync(Guid userId, bool withHold, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var snapshot = await catalog.GetAsync();
        var flag = await payment.GetAsync();
        var held = withHold
            ? await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.BuddyRequestsHeldUntil)
                .FirstOrDefaultAsync(ct)
            : null;
        var activePlanId = await db.UserPlans.AsNoTracking()
            .Where(u => u.UserId == userId && u.FeatureKey == PlanFeatures.Seeker)
            .Where(PlanRowSql.Entitled(now))
            .OrderByDescending(u => u.EndsAt)
            .Select(u => (Guid?)u.PlanId)
            .FirstOrDefaultAsync(ct);
        return Compute(snapshot.PlansOf(PlanFeatures.Seeker), activePlanId, flag.Enabled, held is { } h && h > now);
    }

    // Payment ON: the active plan's values, else the default plan's. Payment OFF: every key = max over the enabled Seeker
    // plans, not Plus, no upgrade. A held account gets the Free numbers; a held Plus member is never asked to buy Plus. A missing
    // key reads its Free default.
    public static SeekerLimits Compute(IReadOnlyList<CatalogPlan> plans, Guid? activePlanId, bool paymentOn, bool held)
    {
        var defaultPlan = plans.FirstOrDefault(p => p.IsDefault);
        var canBuy = plans.Any(p => p.IsEnabled && !p.IsDefault);
        var active = activePlanId is { } id ? plans.FirstOrDefault(p => p.Id == id) : null;
        var isPlus = active is { IsDefault: false };
        if (held) return From(k => Value(defaultPlan, k), false, paymentOn && canBuy && !isPlus);
        if (!paymentOn)
        {
            var enabled = plans.Where(p => p.IsEnabled).ToList();
            return From(k => enabled.Count == 0 ? SeekerPlanKeys.FreeDefault(k) : enabled.Max(p => Value(p, k)), false, false);
        }
        var plan = active ?? defaultPlan;
        return From(k => Value(plan, k), isPlus, !isPlus && canBuy);
    }

    private static int Value(CatalogPlan? plan, string key)
        => plan != null && plan.Features.TryGetValue(key, out var v) ? v : SeekerPlanKeys.FreeDefault(key);

    private static SeekerLimits From(Func<string, int> value, bool isPlus, bool canUpgrade) => new(
        isPlus, canUpgrade,
        value(SeekerPlanKeys.ActiveTrips), value(SeekerPlanKeys.PublicTrips), value(SeekerPlanKeys.JoinRequestsPerMonth),
        value(SeekerPlanKeys.NewChatsPerMonth), value(SeekerPlanKeys.GenderOnlyTrips) > 0, value(SeekerPlanKeys.NearMe) > 0,
        value(SeekerPlanKeys.GroupChat) > 0);
}
