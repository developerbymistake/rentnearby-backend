namespace RentNearBy.Tests.Plans;

public class PlanProrationTests
{
    private const int Cap = PlanRules.MaxRemainingDays;

    [Fact]
    public void CreditValue_Pro12mAfter65Days_Is1643()
    {
        var now = Dates.Utc(2026, 3, 7);
        Assert.Equal(1643, PlanProration.CreditValueInr(1999, 365, now.AddDays(300), now));
    }

    [Fact]
    public void CreditValue_UsesExactTimeLeftNotWholeDays()
    {
        var now = Dates.Utc(2026, 3, 7);
        Assert.Equal(15, PlanProration.CreditValueInr(300, 30, now.AddDays(1.5), now));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(300, 0)]
    [InlineData(-5, 30)]
    public void CreditValue_NothingPaidOrNoDays_IsZero(int paid, int totalDays)
    {
        var now = Dates.Now;
        Assert.Equal(0, PlanProration.CreditValueInr(paid, totalDays, now.AddDays(10), now));
    }

    [Fact]
    public void CreditValue_Ended_IsZero()
        => Assert.Equal(0, PlanProration.CreditValueInr(300, 30, Dates.Now, Dates.Now));

    [Fact]
    public void CreditValue_CappedRowNeverAboveWhatWasPaid()
        => Assert.Equal(300, PlanProration.CreditValueInr(300, 30, Dates.Now.AddDays(60), Dates.Now));

    [Fact]
    public void Switch_DowngradeCreditAboveNewPrice_BecomesBonusDays()
    {
        var s = PlanProration.Switch(listInr: 149, discountInr: 0, creditValueInr: 1643, baseDays: 30, Cap);

        Assert.Equal(149, s.CreditAppliedInr);
        Assert.Equal(0, s.PayableInr);
        Assert.Equal(300, s.BonusDays);
        Assert.Equal(330, s.GrantedDays);
        Assert.Equal(1490, s.BonusValueInr);
        Assert.Equal(4, s.ForfeitedInr);
        Assert.Equal(1639, s.CarriedValueInr);
    }

    [Fact]
    public void Switch_CouponThenCredit_LedgerAddsUpToList()
    {
        var s = PlanProration.Switch(listInr: 500, discountInr: 250, creditValueInr: 280, baseDays: 30, Cap);

        Assert.Equal(250, s.CreditAppliedInr);
        Assert.Equal(0, s.PayableInr);
        Assert.Equal(500, 250 + s.CreditAppliedInr + s.PayableInr);
        Assert.Equal(1, s.BonusDays);
        Assert.Equal(16, s.BonusValueInr);
        Assert.Equal(14, s.ForfeitedInr);
    }

    [Fact]
    public void Switch_CreditBelowNet_PaysTheRestNoRupeeFloor()
    {
        var s = PlanProration.Switch(listInr: 500, discountInr: 0, creditValueInr: 499, baseDays: 30, Cap);

        Assert.Equal(499, s.CreditAppliedInr);
        Assert.Equal(1, s.PayableInr);
        Assert.Equal(0, s.BonusDays);
    }

    [Fact]
    public void Switch_CreditEqualsPrice_PayableZeroNoBonus()
    {
        var s = PlanProration.Switch(listInr: 500, discountInr: 0, creditValueInr: 500, baseDays: 30, Cap);

        Assert.Equal(0, s.PayableInr);
        Assert.Equal(0, s.BonusDays);
        Assert.Equal(0, s.ForfeitedInr);
    }

    [Fact]
    public void Switch_NoCredit_PaysListMinusCoupon()
    {
        var s = PlanProration.Switch(listInr: 299, discountInr: 30, creditValueInr: 0, baseDays: 30, Cap);

        Assert.Equal(269, s.PayableInr);
        Assert.Equal(0, s.CreditAppliedInr);
        Assert.Equal(30, s.GrantedDays);
    }

    [Fact]
    public void Switch_FreePlanCoupon_WholeCreditBecomesDays()
    {
        var s = PlanProration.Switch(listInr: 300, discountInr: 300, creditValueInr: 150, baseDays: 30, Cap);

        Assert.Equal(0, s.CreditAppliedInr);
        Assert.Equal(0, s.PayableInr);
        Assert.Equal(15, s.BonusDays);
    }

    [Fact]
    public void Switch_BonusCappedAtRemainingDaysCap_ReportsForfeit()
    {
        var s = PlanProration.Switch(listInr: 10, discountInr: 0, creditValueInr: 1000, baseDays: 365, Cap);

        Assert.Equal(365, s.BonusDays);
        Assert.Equal(Cap, s.GrantedDays);
        Assert.Equal(10, s.BonusValueInr);
        Assert.Equal(980, s.ForfeitedInr);
    }

    [Fact]
    public void Switch_ZeroListPrice_NoDivision()
    {
        var s = PlanProration.Switch(listInr: 0, discountInr: 0, creditValueInr: 100, baseDays: 30, Cap);

        Assert.Equal(0, s.PayableInr);
        Assert.Equal(0, s.BonusDays);
        Assert.Equal(100, s.ForfeitedInr);
    }

    [Theory]
    [InlineData(149, 30, 300, 1490)]
    [InlineData(500, 30, 1, 16)]
    [InlineData(500, 30, 0, 0)]
    [InlineData(0, 30, 5, 0)]
    public void BonusValue_IsFlooredListRate(int list, int baseDays, int bonusDays, int expected)
        => Assert.Equal(expected, PlanProration.BonusValueInr(list, baseDays, bonusDays));

    [Fact]
    public void CarriedValue_CreditPlusBonusDaysValue()
        => Assert.Equal(149 + 1490, PlanProration.CarriedValueInr(149, 149, 30, 330));

    [Fact]
    public void CarriedValue_NoBonus_IsCredit()
        => Assert.Equal(120, PlanProration.CarriedValueInr(120, 500, 30, 30));
}
