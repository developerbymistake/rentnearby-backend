using System.Globalization;
using System.Text;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Infrastructure.Services.Owner;

// Contract 3.1: every plan key of every plan of one kind as an inline VALUES list (catalog-typed numbers only, never user input),
// with the payment rule of PlanEntitlementService.Compute already applied. Jobs and the reconcile join UserPlans to it instead of
// computing limits per owner in a loop:
//   WITH {Cte} SELECT ... FROM "TaxiListings" l
//   LEFT JOIN "UserPlans" up ON up."UserId" = l."UserId" AND up."FeatureKey" = 'Taxi' AND up."Status" = 'Active' AND up."EndsAt" > now()
//   JOIN pv ON {Join("up")}
// The row with PlanId = Guid.Empty holds the values of an owner without an active plan. Switches are 0 / 1.
public static class PlanKeySql
{
    public const string DefaultName = "pv";

    public static string Cte(PlanCatalogSnapshot snapshot, PaymentFeatureState flag, string featureKey, string name = DefaultName)
    {
        var keys = PlanFeatureKeys.All;
        var sb = new StringBuilder();
        sb.Append(name).Append(" (\"PlanId\"");
        foreach (var k in keys) sb.Append(", \"").Append(k).Append('"');
        sb.Append(") AS (VALUES ");

        var noPlan = flag.Enabled ? snapshot.DefaultValues(featureKey) : PlanFeatureValues.OffValues(snapshot, featureKey);
        AppendRow(sb, Guid.Empty, Effective(noPlan, flag));
        foreach (var plan in snapshot.PlansOf(featureKey))
        {
            var values = flag.Enabled ? plan.Values : PlanFeatureValues.Max(noPlan, plan.Values);
            sb.Append(", ");
            AppendRow(sb, plan.Id, Effective(values, flag));
        }
        sb.Append(')');
        return sb.ToString();
    }

    public static string Join(string userPlanAlias, string name = DefaultName)
        => $"{name}.\"PlanId\" = COALESCE({userPlanAlias}.\"PlanId\", '{Guid.Empty}'::uuid)";

    public static string Column(string key, string name = DefaultName)
        => PlanKeyCatalog.TryOf(key, out _) ? $"{name}.\"{key}\"" : throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown plan feature key");

    // The value Compute would return for this key (clamped to the catalog range, ListingLimit raised while OFF).
    internal static int Value(PlanFeatureValues values, PaymentFeatureState flag, string key)
    {
        if (key == PlanFeatureKeys.ListingLimit)
            return Math.Max(0, flag.Enabled ? values.ListingLimit : Math.Max(values.ListingLimit, flag.FreeModeListingLimit ?? 0));
        if (key == PlanFeatureKeys.Priority) return Math.Clamp(values.Priority, 0, short.MaxValue);
        if (key == PlanFeatureKeys.JobsPosts) return Math.Max(0, values.JobsPosts);
        var info = PlanKeyCatalog.Of(key);
        return info.IsSwitch ? (values.Get(key) > 0 ? 1 : 0) : info.Clamp(values.Get(key));
    }

    private static int[] Effective(PlanFeatureValues values, PaymentFeatureState flag)
        => PlanFeatureKeys.All.Select(k => Value(values, flag, k)).ToArray();

    private static void AppendRow(StringBuilder sb, Guid planId, int[] values)
    {
        sb.Append("('").Append(planId).Append("'::uuid");
        foreach (var v in values) sb.Append(", ").Append(v.ToString(CultureInfo.InvariantCulture));
        sb.Append(')');
    }
}
