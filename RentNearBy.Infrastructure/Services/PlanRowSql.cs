using System.Linq.Expressions;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;

namespace RentNearBy.Infrastructure.Services;

// D-10 / FB-15: the one "entitled plan row" predicate. An Active row counts while its end date is ahead, and a Google Play
// row also past it (grace, late renewal) until the Play sync or the 72 hour net ends it in one transaction with the plan
// effects. Every limits, ranking and plan-rows read uses this, in C# (Entitled) and in SQL (EntitledSql / ActiveLateral).
public static class PlanRowSql
{
    public static Expression<Func<UserPlan, bool>> Entitled(DateTime nowUtc)
        => u => u.Status == UserPlanStatuses.Active && (u.EndsAt > nowUtc || u.PlaySubscriptionId != null);

    // EntitledSql("u") as a constant, for SQL texts that are constants themselves.
    public const string EntitledSqlConst = "u.\"Status\" = '" + UserPlanStatuses.Active + "' AND (u.\"EndsAt\" > now() OR u.\"PlaySubscriptionId\" IS NOT NULL)";

    public static string EntitledSql(string alias)
        => $"{alias}.\"Status\" = '{UserPlanStatuses.Active}' AND ({alias}.\"EndsAt\" > now() OR {alias}.\"PlaySubscriptionId\" IS NOT NULL)";

    // The owner's entitled row (one at most) as lateral `up` with the columns PlanKeySql.Join reads.
    public static string ActiveLateral(string userIdExpression, string kind, string alias = "up") => $"""
        LEFT JOIN LATERAL (
            SELECT p."PlanId", p."Source" FROM "UserPlans" p
            WHERE p."UserId" = {userIdExpression} AND p."FeatureKey" = '{kind}' AND {EntitledSql("p")}
            ORDER BY p."EndsAt" DESC LIMIT 1) {alias} ON true
        """;
}
