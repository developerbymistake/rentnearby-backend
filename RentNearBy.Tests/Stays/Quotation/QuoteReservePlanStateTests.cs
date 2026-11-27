using Microsoft.EntityFrameworkCore;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Core.Models.Owner.Quote;
using RentNearBy.Infrastructure.Services;
using RentNearBy.Tests.Data;
using RentNearBy.Tests.Integration;
using RentNearBy.Tests.Owner;
using RentNearBy.Tests.Support.Owner;

namespace RentNearBy.Tests.Stays.Quotation;

// Q13 / Q16 PLAN 3 STATES RULE: the cap comes from the plan key StayQuotationsPerMonth through the one entitlement maths:
// Free 2; Plus active / cancelled-in-period / grace 30; ended (on hold, paused, expired: the applier ends the row) back to 2 with
// the month's count kept; re-subscribed 30 at once; payment OFF 30 for everyone without an upsell; admin grant 30.
public class QuoteReservePlanStateTests(TestDatabase db) : IntegrationTestBase(db)
{
    private async Task<RentNearBy.Infrastructure.Services.Owner.Quote.QuoteResult<RentNearBy.Core.DTOs.Owner.Quote.QuoteReserveDto>> Reserve(
        Guid user, bool paymentOn = true)
    {
        await using var c = CreateContext();
        return await QuoteKit.Reserve(c, paymentOn: paymentOn).ReserveAsync(user, Guid.NewGuid(), QuoteKinds.QuotationCode, default);
    }

    private async Task<(int Limit, bool CanUpgrade)> CapOf(Guid user, bool paymentOn = true)
    {
        var r = await Reserve(user, paymentOn);
        Assert.True(r.Ok, r.ErrorType);
        return (r.Value!.Usage.Limit, r.Value.Usage.CanUpgrade);
    }

    [IntegrationFact]
    [PlanKeyTest(PlanFeatureKeys.StayQuotationsPerMonth)]
    public async Task Free_two_plus_thirty_payment_off_thirty_without_upsell()
    {
        var free = await QuoteKit.ReadyOwnerAsync(CreateContext);
        Assert.Equal((2, true), await CapOf(free));

        var plus = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using (var c = CreateContext()) await QuoteKit.PlanAsync(c, plus, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(20));
        Assert.Equal((30, false), await CapOf(plus));

        var off = await QuoteKit.ReadyOwnerAsync(CreateContext);
        Assert.Equal((30, false), await CapOf(off, paymentOn: false));
    }

    // Cancelled with auto-renew off but inside the paid period: the UserPlans row stays Active until EndsAt.
    [IntegrationFact]
    public async Task Cancelled_inside_the_paid_period_keeps_thirty()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using (var c = CreateContext()) await QuoteKit.PlanAsync(c, user, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(3));
        Assert.Equal(30, (await CapOf(user)).Limit);
    }

    [IntegrationFact]
    public async Task Admin_grant_keeps_thirty()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using (var c = CreateContext())
            await QuoteKit.PlanAsync(c, user, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(30), source: UserPlanSources.AdminGrant);
        Assert.Equal(30, (await CapOf(user)).Limit);
        Assert.Equal(30, (await CapOf(user, paymentOn: false)).Limit);
    }

    // 7 used on Plus, then Plus ends: back to Free 2 at once with 7 kept, so the next new document is refused (never "7 of 2" on
    // the server: data says limit 2, used 7, canUpgrade true). Re-subscribing in the same month gives 30 again with the same count.
    [IntegrationFact]
    public async Task Ended_plus_keeps_the_count_and_resubscribing_gives_thirty_again()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        await using (var c = CreateContext()) await QuoteKit.PlanAsync(c, user, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(20));
        for (var i = 0; i < 7; i++) Assert.True((await Reserve(user)).Ok);

        await using (var c = CreateContext())
            await c.UserPlans.Where(u => u.UserId == user).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Status, UserPlanStatuses.Expired).SetProperty(u => u.EndsAt, DateTime.UtcNow.AddMinutes(-1)));
        var refused = await Reserve(user);
        Assert.Equal(QuoteErrors.MonthLimit, refused.ErrorType);
        var data = Assert.IsType<RentNearBy.Core.DTOs.Owner.Quote.QuoteLimitDataDto>(refused.Data);
        Assert.Equal((2, 7, true), (data.Limit, data.Used, data.CanUpgrade));

        await using (var c = CreateContext()) await QuoteKit.PlanAsync(c, user, UserPlanStatuses.Active, DateTime.UtcNow.AddDays(30));
        var again = (await Reserve(user)).Value!;
        Assert.Equal((30, 8), (again.Usage.Limit, again.Usage.Used));
    }

    // Grace / late renewal: a Play row past EndsAt stays entitled (PlanRowSql) until the Play sync ends it.
    [Fact]
    public void Grace_row_past_its_end_is_still_entitled_and_computes_thirty()
    {
        var now = DateTime.UtcNow;
        var row = new UserPlan
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), FeatureKey = ListingKinds.Stay, PlanId = OwnerPlanCatalog.StayPlusId,
            Source = UserPlanSources.Subscription, Status = UserPlanStatuses.Active, StartsAt = now.AddDays(-31), EndsAt = now.AddDays(-1),
            PlaySubscriptionId = Guid.NewGuid(),
        };
        Assert.True(PlanRowSql.Entitled(now).Compile()(row));
        var limits = PlanEntitlementService.Compute(OwnerPlanCatalog.Seeded(), PaymentFeatureStates.On(), ListingKinds.Stay,
            new PlanEntitlementService.ActiveRow(row.Id, row.UserId, row.PlanId, row.Source, row.StartsAt, row.EndsAt));
        Assert.Equal(30, limits.StayQuotationsPerMonth);
        Assert.False(limits.CanUpgrade);
    }

    [Fact]
    public void Catalog_seed_is_free_two_plus_thirty_and_off_takes_the_max()
    {
        var info = PlanKeyCatalog.Of(PlanFeatureKeys.StayQuotationsPerMonth);
        Assert.Equal((PlanKeyKind.Limit, 2, 30, "Quotations a month"), (info.Kind, info.Stay!.Free, info.Stay.Plus, info.Label));
        Assert.Null(info.Taxi);
        var snapshot = OwnerPlanCatalog.Seeded();
        Assert.Equal(2, PlanEntitlementService.Compute(snapshot, PaymentFeatureStates.On(), ListingKinds.Stay, null).StayQuotationsPerMonth);
        Assert.Equal(30, PlanEntitlementService.Compute(snapshot, PaymentFeatureStates.Off(), ListingKinds.Stay, null).StayQuotationsPerMonth);
        Assert.Equal(2, PlanFeatureValues.Fallback.StayQuotationsPerMonth);
    }

    [IntegrationFact]
    public async Task Plans_me_carries_the_cap_the_month_count_and_the_reset()
    {
        var user = await QuoteKit.ReadyOwnerAsync(CreateContext);
        Assert.True((await Reserve(user)).Ok);
        await using var c = CreateContext();
        var usage = await RentNearBy.Infrastructure.Services.Owner.Quote.StayQuoteUsageReader.ReadAsync(c, user, DateTime.UtcNow);
        Assert.Equal(1, usage.Used);
        Assert.Equal(RentNearBy.Core.Models.Social.SocialMonth.ResetsAtUtc(DateTime.UtcNow), usage.ResetsAt);
        var json = System.Text.Json.JsonSerializer.Serialize(new RentNearBy.Core.DTOs.Responses.MyPlanLimitsDto { StayQuotationsPerMonth = 2 },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"quotationsPerMonth\":2", json);
        Assert.Contains("\"quotationsUsed\":0", json);
        Assert.Contains("\"quotationsResetsAt\":null", json);
    }
}
