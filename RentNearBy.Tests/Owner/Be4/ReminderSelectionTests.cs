using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Owner.Be4;

// Contract 3.2 Reminders: the morning reminder job selects an owner only when his plan value Reminders is 1 (Free = never, Plus = yes,
// payment OFF = everyone), for both roles.
public class ReminderSelectionTests
{
    [Theory]
    [InlineData(ListingKinds.Stay)]
    [InlineData(ListingKinds.Taxi)]
    [PlanKeyTest(PlanFeatureKeys.Reminders)]
    public void Reminders_job_selects_only_owners_whose_plan_value_is_on(string kind)
    {
        var snapshot = OwnerPlanCatalog.Seeded();
        var plusId = kind == ListingKinds.Stay ? OwnerPlanCatalog.StayPlusId : OwnerPlanCatalog.TaxiPlusId;

        var on = PlanKeySql.Cte(snapshot, PaymentFeatureStates.On(), kind);
        Assert.Equal(0, RemindersOf(on, Guid.Empty));
        Assert.Equal(1, RemindersOf(on, plusId));

        var off = PlanKeySql.Cte(snapshot, PaymentFeatureStates.Off(), kind);
        Assert.Equal(1, RemindersOf(off, Guid.Empty));
        Assert.Equal(1, RemindersOf(off, plusId));

        var sql = Job(kind).BatchSql(on);
        Assert.Contains($"AND pv.\"{PlanFeatureKeys.Reminders}\" = 1", sql);
        Assert.Contains("JOIN pv ON pv.\"PlanId\" = COALESCE(up.\"PlanId\"", sql);
        Assert.Contains($"p.\"FeatureKey\" = '{kind}' AND p.\"Status\" = '{UserPlanStatuses.Active}' AND (p.\"EndsAt\" > now() OR p.\"PlaySubscriptionId\" IS NOT NULL)", sql);
        Assert.Contains("l.\"RemindersOn\"", sql);
    }

    private static OwnerMorningReminderJob Job(string kind)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new OwnerFeatureOptions());
        return kind == ListingKinds.Stay
            ? new StayMorningReminderJob(null!, null!, null!, null!, options, TimeProvider.System, NullLogger<StayMorningReminderJob>.Instance)
            : new TaxiMorningReminderJob(null!, null!, null!, null!, options, TimeProvider.System, NullLogger<TaxiMorningReminderJob>.Instance);
    }

    // The Reminders column of the plan's row in the inline VALUES list.
    private static int RemindersOf(string cte, Guid planId)
    {
        // The first value after the id is the Grant column (0 = any row but an admin grant).
        var row = Regex.Match(cte, $@"\('{planId}'::uuid, 0((?:, -?\d+)+)\)");
        Assert.True(row.Success, "No row for plan " + planId);
        var values = row.Groups[1].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        return values[PlanFeatureKeys.All.ToList().IndexOf(PlanFeatureKeys.Reminders)];
    }
}
