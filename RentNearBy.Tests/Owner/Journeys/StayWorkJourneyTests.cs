using System.Text.Json;
using RentNearBy.Core.DTOs.Owner;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// JT-B-ST-2 and the plan-card parts of JT-B-ST-1 / JT-B-TX-1 (FB-12, FB-17, FB-26, FB-34, FB-39, FB-50).
public class StayWorkJourneyTests
{
    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    [Fact]
    public void Centre_badge_counts_asked_countered_and_change_requests()
        => Assert.Contains("CentreBadge = work.AskedCount + work.CounteredCount + work.ChangeRequests",
            Src("RentNearBy.Infrastructure", "Services", "Owner", "Be4", "StayOwnerHomeReader.cs"));

    [Theory]
    [InlineData("StayOwnerHomeReader.cs")]
    [InlineData("TaxiDriverHomeReader.cs")]
    public void No_plan_card_while_the_tab_is_off(string file)
        => Assert.Contains("if (approved && !areaPaused && env.TabActive)", Src("RentNearBy.Infrastructure", "Services", "Owner", "Be4", file));

    [Fact]
    public void Plan_extras_read_the_play_trial_and_the_unclaimed_end()
    {
        var src = Src("RentNearBy.Infrastructure", "Services", "Owner", "Be4", "OwnerHomeShared.cs");
        Assert.Contains("u.\"PlayTrialEndsAt\"", src);
        Assert.Contains("u.\"PlaySubscriptionId\" IS NULL AND u.\"EndsAt\" <= now()", src);
    }

    [Fact]
    public void Plans_page_uses_the_home_state_rule()
        => Assert.Contains("dto.State = Owner.Be4.OwnerHomeShared.PlanCardState(limits, flag, extras);",
            Src("RentNearBy.Infrastructure", "Services", "PlanPurchaseService.cs"));

    [Fact]
    public void Special_price_list_carries_the_state_name()
    {
        var dto = new SpecialPriceListDto([], [], 3, [], true, "Uttarakhand");
        var json = JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("Uttarakhand", json.GetProperty("stateName").GetString());
    }
}
