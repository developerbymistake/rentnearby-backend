namespace RentNearBy.Tests.Plans;

public class PlanTimeTests
{
    private static DateTime Ist(int y, int m, int d, int h = 0, int min = 0) => Dates.Utc(y, m, d, h, min).AddMinutes(-330);

    [Fact]
    public void DaysLeft_IsIstCalendarDays_NotCeilingOfFraction()
        => Assert.Equal(3, PlanTime.DaysLeft(Ist(2026, 11, 12, 10), Ist(2026, 11, 9, 9)));

    [Fact]
    public void DaysLeft_SameIstDayLater_IsZero()
        => Assert.Equal(0, PlanTime.DaysLeft(Ist(2026, 11, 12, 22), Ist(2026, 11, 12, 1)));

    [Fact]
    public void DaysLeft_Ended_IsZero()
        => Assert.Equal(0, PlanTime.DaysLeft(Ist(2026, 11, 12), Ist(2026, 11, 13)));

    [Fact]
    public void DaysLeft_CrossesUtcMidnightButNotIst()
        => Assert.Equal(1, PlanTime.DaysLeft(Ist(2026, 11, 13, 4), Ist(2026, 11, 12, 23, 30)));

    [Fact]
    public void IstMonthStart_FirstOfMonthBeforeFiveThirtyIst_IsThisMonth()
    {
        var now = Ist(2026, 12, 1, 3);
        Assert.Equal(Dates.Utc(2026, 11, 30, 18, 30), PlanTime.IstMonthStartUtc(now));
    }

    [Fact]
    public void IstMonthStart_ChargeAt0200IstFirstDec_CountsInDecember()
    {
        var charge = Ist(2026, 12, 1, 2);
        Assert.True(charge >= PlanTime.IstMonthStartUtc(Ist(2026, 12, 1, 3)));
    }

    [Fact]
    public void IstMonthStart_LastIstMinuteOfNovember_IsNovember()
        => Assert.Equal(Dates.Utc(2026, 10, 31, 18, 30), PlanTime.IstMonthStartUtc(Ist(2026, 11, 30, 23, 59)));

    [Fact]
    public void IstMonthStart_YearEnd_NewYearInIst()
        => Assert.Equal(Dates.Utc(2026, 12, 31, 18, 30), PlanTime.IstMonthStartUtc(Dates.Utc(2026, 12, 31, 19)));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(11, 0)]
    [InlineData(12, 1)]
    [InlineData(23, 1)]
    [InlineData(24, 1)]
    [InlineData(10 * 24 + 23, 11)]
    public void WholeDays_RoundsToNearestHalfUp(int hours, int expected)
        => Assert.Equal(expected, PlanTime.WholeDays(TimeSpan.FromHours(hours)));

    [Fact]
    public void WholeDays_ThirtyDaysMinusASecond_IsThirty()
        => Assert.Equal(30, PlanTime.WholeDays(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1)));

    [Theory]
    [InlineData(2026, 1, 31, 1, 28)]
    [InlineData(2028, 1, 31, 1, 29)]
    [InlineData(2026, 3, 1, 1, 31)]
    [InlineData(2026, 4, 1, 1, 30)]
    [InlineData(2026, 1, 1, 3, 90)]
    [InlineData(2028, 1, 1, 12, 366)]
    [InlineData(2026, 1, 1, 12, 365)]
    public void CalendarCycleDays_FollowsTheCalendar(int y, int m, int d, int months, int expected)
        => Assert.Equal(expected, PlanTime.CalendarCycleDays(Dates.Utc(y, m, d, 10), months));

    [Fact]
    public void PlayPeriodDays_KnownStart_IsExact()
        => Assert.Equal(28, PlanTime.PlayPeriodDays(Dates.Utc(2026, 1, 31, 10), Dates.Utc(2026, 2, 28, 10), 1));

    [Fact]
    public void PlayPeriodDays_RenewalSpan_IsThirtyNotTwentyNine()
        => Assert.Equal(30, PlanTime.PlayPeriodDays(Dates.Utc(2026, 1, 30, 10), Dates.Utc(2026, 3, 1, 10, 0, 5), 1));

    [Fact]
    public void PlayPeriodDays_StartOlderThanOneCycle_UsesCalendarCycleEndingAtEnd()
        => Assert.Equal(31, PlanTime.PlayPeriodDays(Dates.Utc(2025, 6, 1), Dates.Utc(2026, 4, 1), 1));

    [Fact]
    public void PlayPeriodDays_NoStart_UsesCalendarCycle()
        => Assert.Equal(90, PlanTime.PlayPeriodDays(null, Dates.Utc(2026, 4, 1), 3));

    [Fact]
    public void PlayPeriodDays_StartAfterEnd_UsesCalendarCycle()
        => Assert.Equal(31, PlanTime.PlayPeriodDays(Dates.Utc(2026, 5, 1), Dates.Utc(2026, 4, 1), 1));
}
