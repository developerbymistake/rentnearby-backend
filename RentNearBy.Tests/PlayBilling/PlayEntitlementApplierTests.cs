using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.PlayBilling;

public class PlayEntitlementApplierTests
{
    private const int Mirror = 299;

    [Theory]
    [InlineData(149_000_000L, "INR", 149)]
    [InlineData(149_500_000L, "INR", 150)]
    [InlineData(149_499_999L, "INR", 149)]
    [InlineData(1L, "INR", 0)]
    [InlineData(149_000_000L, "USD", Mirror)]
    [InlineData(149_000_000L, null, Mirror)]
    [InlineData(null, "INR", Mirror)]
    [InlineData(0L, "INR", Mirror)]
    [InlineData(-1L, "INR", Mirror)]
    public void PayableOf_GoogleInrPriceElseMirror(long? micros, string? currency, int expected)
    {
        var item = new PlayLineItemBuilder().WithPrice(micros, currency).Build();

        Assert.Equal(expected, PlayEntitlementApplier.PayableOf(item, Mirror));
    }

    [Theory]
    [InlineData(7, true, 1, 7)]
    [InlineData(0, true, 1, 1)]
    [InlineData(-3, true, 1, 1)]
    [InlineData(7, false, 1, 30)]
    [InlineData(7, false, 3, 90)]
    [InlineData(7, false, 12, 365)]
    public void DaysOf_TrialUsesTrialDaysElseCycle(int trialDays, bool trial, int cycleMonths, int expected)
    {
        var plan = CatalogPlanBuilder.Plus().WithTrialDays(trialDays).Build();
        var price = new CatalogPriceBuilder().WithCycle((short)cycleMonths).Build();
        var target = new PlayResolvedTargetBuilder().WithPlan(plan).WithPrice(price).Build();

        Assert.Equal(expected, PlayEntitlementApplier.DaysOf(target, trial));
    }
}
