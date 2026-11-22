using System.Globalization;
using System.Text;
using RentNearBy.Core.Interfaces;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;

namespace RentNearBy.Infrastructure.Services.Owner;

// Contract 3.1: every plan key of every plan of one kind as an inline VALUES list (catalog-typed numbers only, never user input),
// with the payment rule of PlanEntitlementService.Compute (PlanFeatureValues.Effective) already applied. Jobs and the reconcile
// join the owner's entitled row to it instead of computing limits per owner in a loop:
//   WITH {Cte} SELECT ... FROM "TaxiListings" l {PlanRowSql.ActiveLateral("l.\"UserId\"", "Taxi")} JOIN pv ON {Join("up")}
// The row with PlanId = Guid.Empty holds the values of an owner without an active plan. Each plan has two rows, Grant 0 and 1
// (D-24: while payment is OFF only an admin grant raises the ranking keys). Switches are 0 / 1.
public static class PlanKeySql
{
    public const string DefaultName = "pv";

    public static string Cte(PlanCatalogSnapshot snapshot, PaymentFeatureState flag, string featureKey, string name = DefaultName)
    {
        var keys = PlanFeatureKeys.All;
        var sb = new StringBuilder();
        sb.Append(name).Append(" (\"PlanId\", \"Grant\"");
        foreach (var k in keys) sb.Append(", \"").Append(k).Append('"');
        sb.Append(") AS (VALUES ");

        var noPlan = flag.Enabled ? snapshot.DefaultValues(featureKey) : PlanFeatureValues.OffValues(snapshot, featureKey);
        AppendRow(sb, Guid.Empty, 0, Effective(noPlan, flag));
        foreach (var plan in snapshot.PlansOf(featureKey))
        {
            foreach (var grant in new[] { false, true })
            {
                var values = PlanFeatureValues.Effective(flag.Enabled, noPlan, plan.Values, grant);
                sb.Append(", ");
                AppendRow(sb, plan.Id, grant ? 1 : 0, Effective(values, flag));
            }
        }
        sb.Append(')');
        return sb.ToString();
    }

    // The joined row needs "PlanId" and "Source" (PlanRowSql.ActiveLateral selects both).
    public static string Join(string userPlanAlias, string name = DefaultName)
        => $"{name}.\"PlanId\" = COALESCE({userPlanAlias}.\"PlanId\", '{Guid.Empty}'::uuid)"
           + $" AND {name}.\"Grant\" = CASE WHEN {userPlanAlias}.\"Source\" = '{UserPlanSources.AdminGrant}' THEN 1 ELSE 0 END";

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

    private static void AppendRow(StringBuilder sb, Guid planId, int grant, int[] values)
    {
        sb.Append("('").Append(planId).Append("'::uuid, ").Append(grant.ToString(CultureInfo.InvariantCulture));
        foreach (var v in values) sb.Append(", ").Append(v.ToString(CultureInfo.InvariantCulture));
        sb.Append(')');
    }
}
