using RentNearBy.Core.Models;
using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Repositories;
using RentNearBy.Infrastructure.Services.Retention;

namespace RentNearBy.Tests.Owner.Be2;

// Contract 1.3 / 5.1: a special price goes 30 days after its last night (the rule lives in lane BE1's owner retention file;
// this pins what pricing relies on: exactly one rule, the end-date predicate and its index order, group D).
public class SpecialPriceRetentionTests
{
    [Fact]
    public void Finished_special_prices_are_cleaned_30_days_after_their_last_night()
    {
        var rule = Assert.Single(RetentionRuleRegistration.Discover(typeof(StayListingRepository).Assembly), r => r.Name == "StaySpecialPrices");
        var def = Assert.IsType<DeclarativeRetentionRule>(rule).Definition;
        Assert.Equal("StaySpecialPrices", def.Table);
        Assert.Equal(RetentionGroups.OwnerCalendar, def.Group);
        Assert.Equal(OwnerLimits.SpecialDays, def.DefaultDays);
        Assert.Equal(30, OwnerLimits.SpecialDays);
        Assert.Contains("\"DateTo\" < @cutoffDate", def.EligibleSql);
        Assert.Contains("DateTo", def.OrderBy);
        Assert.True(RetentionSchedule.OrderOf("StaySpecialPrices") > RetentionSchedule.OrderOf("Chat"));
    }
}
