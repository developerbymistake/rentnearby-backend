namespace RentNearBy.Tests.Trips;

public class TripReminderScheduleTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Morning_clock_is_05_00_ist_in_one_place()
    {
        Assert.Equal(5, TripReminderRules.MorningHourIst);
        Assert.Equal(0, TripReminderRules.MorningMinuteIst);
        // 05:00 IST = 23:30 UTC the day before.
        Assert.Equal(Utc(2026, 11, 11, 23, 30), TripReminderRules.MorningDueUtc(new DateOnly(2026, 11, 12)));
    }

    [Fact]
    public void Next_morning_is_today_when_still_ahead_else_tomorrow()
    {
        Assert.Equal(Utc(2026, 11, 11, 23, 30), TripReminderRules.NextMorningUtc(Utc(2026, 11, 11, 20, 0)));
        Assert.Equal(Utc(2026, 11, 12, 23, 30), TripReminderRules.NextMorningUtc(Utc(2026, 11, 11, 23, 30)));
        Assert.Equal(Utc(2026, 11, 12, 23, 30), TripReminderRules.NextMorningUtc(Utc(2026, 11, 12, 4, 0)));
    }

    [Fact]
    public void Changing_the_hour_to_08_needs_no_other_change()
    {
        // 08:00 IST = 02:30 UTC.
        Assert.Equal(Utc(2026, 11, 12, 2, 30), TripReminderRules.NextMorningUtc(Utc(2026, 11, 11, 20, 0), 8, 0));
        Assert.Equal(Utc(2026, 11, 13, 2, 30), TripReminderRules.NextMorningUtc(Utc(2026, 11, 12, 2, 30), 8, 0));
        Assert.Equal(Utc(2026, 11, 12, 2, 30), TripReminderRules.MorningDueUtc(new DateOnly(2026, 11, 12), 8, 0));
    }

    [Theory]
    [InlineData(23, 29, false)] // 04:59 IST, before the time
    [InlineData(23, 30, true)]  // 05:00 IST
    public void Run_starts_at_the_morning_time(int hour, int minute, bool expected)
        => Assert.Equal(expected, TripReminderRules.ShouldRunMorning(Utc(2026, 11, 11, hour, minute), false));

    [Fact]
    public void Catch_up_after_a_restart_runs_before_noon_only()
    {
        Assert.True(TripReminderRules.ShouldRunMorning(Utc(2026, 11, 12, 1, 0), false));    // 06:30 IST
        Assert.True(TripReminderRules.ShouldRunMorning(Utc(2026, 11, 12, 6, 29), false));   // 11:59 IST
        Assert.False(TripReminderRules.ShouldRunMorning(Utc(2026, 11, 12, 6, 30), false));  // 12:00 IST
        Assert.False(TripReminderRules.ShouldRunMorning(Utc(2026, 11, 12, 10, 0), false));  // afternoon
    }

    [Fact]
    public void A_finished_day_never_runs_again()
        => Assert.False(TripReminderRules.ShouldRunMorning(Utc(2026, 11, 12, 1, 0), true));

    [Fact]
    public void Catch_up_window_follows_a_later_hour()
    {
        Assert.False(TripReminderRules.ShouldRunMorning(Utc(2026, 11, 12, 1, 0), false, 8, 0)); // 06:30 IST, before 08:00
        Assert.True(TripReminderRules.ShouldRunMorning(Utc(2026, 11, 12, 2, 30), false, 8, 0));
    }

    [Fact]
    public void Keys_carry_the_ist_day_so_one_push_per_person_per_day()
    {
        var user = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var day = new DateOnly(2026, 11, 12);
        Assert.Equal("rem:t:11111111222233334444555555555555:20261112", TripReminderRules.TravellerKey(user, day));
        Assert.Equal("rem:h:11111111222233334444555555555555:20261112", TripReminderRules.HostKey(user, day));
        Assert.NotEqual(TripReminderRules.TravellerKey(user, day), TripReminderRules.TravellerKey(user, day.AddDays(1)));
        Assert.NotEqual(TripReminderRules.TravellerKey(user, day), TripReminderRules.HostKey(user, day));
    }
}
