namespace RentNearBy.Tests.Trips;

public class IstTimeAndReminderWindowTests
{
    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0, int s = 0) => new(y, m, d, h, min, s, DateTimeKind.Utc);

    [Fact]
    public void Ist_midnight_and_today_flip_at_18_30_utc()
    {
        Assert.Equal(Utc(2026, 11, 11, 18, 30), IstTime.IstMidnightUtc(new DateOnly(2026, 11, 12)));
        Assert.Equal(new DateOnly(2026, 11, 11), IstTime.TodayIst(Utc(2026, 11, 11, 18, 29, 59)));
        Assert.Equal(new DateOnly(2026, 11, 12), IstTime.TodayIst(Utc(2026, 11, 11, 18, 30)));
        Assert.Equal(Utc(2026, 11, 12, 1, 30), IstTime.AtUtc(new DateOnly(2026, 11, 12), 7));
    }

    [Fact]
    public void Eve_window_is_18_00_to_24_00_ist_the_day_before()
    {
        var travel = new DateOnly(2026, 11, 12);
        Assert.False(IstTime.InEveWindow(travel, Utc(2026, 11, 11, 12, 29)));
        Assert.True(IstTime.InEveWindow(travel, Utc(2026, 11, 11, 12, 30)));
        Assert.True(IstTime.InEveWindow(travel, Utc(2026, 11, 11, 18, 29)));
        Assert.False(IstTime.InEveWindow(travel, Utc(2026, 11, 11, 18, 30)));
        Assert.Equal(TripReminderRules.TravelEveInWindow(travel, Utc(2026, 11, 11, 15, 0)), IstTime.InEveWindow(travel, Utc(2026, 11, 11, 15, 0)));
    }

    [Fact]
    public void Journey_eve_no_longer_fires_just_after_midnight()
    {
        var travel = new DateOnly(2026, 11, 12);
        // 00:05 IST on 11 Nov (the old date-only rule sent here) and 23:50 IST on 10 Nov are outside; 18:00 IST on 11 Nov is in.
        Assert.False(JourneyEveRules.InWindow(travel, Utc(2026, 11, 10, 18, 35)));
        Assert.False(JourneyEveRules.InWindow(travel, Utc(2026, 11, 10, 18, 20)));
        Assert.True(JourneyEveRules.InWindow(travel, Utc(2026, 11, 11, 12, 30)));
        Assert.False(JourneyEveRules.InWindow(travel, Utc(2026, 11, 12, 3, 0)));
    }

    [Fact]
    public void Journey_eve_key_carries_the_date()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var key = JourneyEveRules.Key(id, new DateOnly(2026, 11, 12));
        Assert.Equal("jrn:e:11111111222233334444555555555555:20261112", key);
        Assert.True(key.Length <= 80);
        Assert.NotEqual(key, JourneyEveRules.Key(id, new DateOnly(2026, 11, 13)));
    }

    [Fact]
    public void Quiet_hours_wrap_midnight_and_wake_at_seven_ist()
    {
        Assert.False(IstTime.IsQuiet(Utc(2026, 11, 11, 16, 29), 22, 7));
        Assert.True(IstTime.IsQuiet(Utc(2026, 11, 11, 16, 30), 22, 7));
        Assert.True(IstTime.IsQuiet(Utc(2026, 11, 12, 1, 29), 22, 7));
        Assert.False(IstTime.IsQuiet(Utc(2026, 11, 12, 1, 30), 22, 7));
        Assert.False(IstTime.IsQuiet(Utc(2026, 11, 12, 1, 30), 7, 7));

        Assert.Equal(Utc(2026, 11, 12, 1, 30), IstTime.WakeUtc(Utc(2026, 11, 11, 17, 30), 22, 7));
        Assert.Equal(Utc(2026, 11, 12, 1, 30), IstTime.WakeUtc(Utc(2026, 11, 11, 20, 30), 22, 7));
        Assert.Equal(Utc(2026, 11, 12, 4, 30), IstTime.WakeUtc(Utc(2026, 11, 12, 4, 30), 22, 7));
    }

    [Fact]
    public void Starts_today_window_closes_with_the_start_day()
    {
        var start = new DateOnly(2026, 11, 12);
        var due = TripReminderRules.TodayDueUtc(start);
        var end = Utc(2026, 11, 12, 18, 30);
        Assert.False(TripReminderRules.TodayOpen(start, due.AddSeconds(-1)));
        Assert.True(TripReminderRules.TodayOpen(start, due));
        Assert.True(TripReminderRules.TodayOpen(start, end.AddSeconds(-1)));
        Assert.False(TripReminderRules.TodayOpen(start, end));

        // The audit case: 08:00 IST on 13 Nov sat inside the old 48 h window; "starts today" must not go out then.
        var nextDay = Utc(2026, 11, 13, 2, 30);
        Assert.True(TripReminderRules.InWindow(due, nextDay));
        Assert.False(TripReminderRules.TodayOpen(start, nextDay));
    }

    [Fact]
    public void Mark_complete_window_closes_at_the_end_of_the_day_after_the_trip()
    {
        var end = new DateOnly(2026, 11, 12);
        var due = TripReminderRules.MarkCompleteDueUtc(end);
        Assert.Equal(Utc(2026, 11, 13, 4, 30), due);
        Assert.False(TripReminderRules.MarkCompleteOpen(end, due.AddSeconds(-1)));
        Assert.True(TripReminderRules.MarkCompleteOpen(end, due));
        Assert.True(TripReminderRules.MarkCompleteOpen(end, Utc(2026, 11, 13, 18, 29, 59)));
        Assert.False(TripReminderRules.MarkCompleteOpen(end, Utc(2026, 11, 13, 18, 30)));
    }

    [Fact]
    public void Starts_in_keeps_its_48_hour_catch_up()
    {
        var start = new DateOnly(2026, 11, 12);
        var due = TripReminderRules.StartsInDueUtc(start);
        Assert.True(TripReminderRules.StartsInOpen(start, due.AddHours(47)));
        Assert.False(TripReminderRules.StartsInOpen(start, due.AddHours(48)));
    }

    [Fact]
    public void Open_date_sets_list_exactly_the_dates_whose_window_is_open()
    {
        var nineAm = Utc(2026, 11, 9, 3, 30);
        Assert.Equal(new[] { new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12) }, TripReminderRules.StartsInOpenDates(nineAm));
        Assert.Equal(new[] { new DateOnly(2026, 11, 9) }, TripReminderRules.TodayOpenDates(nineAm));
        Assert.Empty(TripReminderRules.TodayOpenDates(Utc(2026, 11, 9, 0, 30)));
        Assert.Empty(TripReminderRules.MarkCompleteOpenDates(nineAm));
        Assert.Equal(new[] { new DateOnly(2026, 11, 8) }, TripReminderRules.MarkCompleteOpenDates(Utc(2026, 11, 9, 5, 30)));
    }

    // The due-queries build these keys in SQL from uuid::text (Guid "D", lowercase) and replace(uuid::text, '-', '') (Guid "N").
    [Fact]
    public void Reminder_keys_match_the_sql_key_expressions()
    {
        var id = Guid.Parse("ABCDEF01-2222-3333-4444-555555555555");
        var trip = Guid.Parse("11111111-2222-3333-4444-5555555555AA");
        var date = new DateOnly(2026, 11, 12);
        var d = id.ToString("D").ToLowerInvariant();
        var t = trip.ToString("D").ToLowerInvariant();

        Assert.Equal("noreply:" + d, TripReminderRules.NoReplyKey(id));
        Assert.Equal("noreply:seg:" + t.Replace("-", "") + ":20261112", TripReminderRules.NoReplySegmentKey(trip, date));
        Assert.Equal("hostnc:" + d, TripReminderRules.HostNotConfirmedKey(id));
        Assert.Equal("d3:" + t + ":20261112", TripReminderRules.StartsInKey(trip, date));
        Assert.Equal("d0:" + t + ":20261112", TripReminderRules.TodayKey(trip, date));
        Assert.Equal("done:" + t + ":20261112", TripReminderRules.MarkCompleteKey(trip, date));
        Assert.Equal(JourneyEveRules.KeyPrefix + t.Replace("-", "") + ":20261112", JourneyEveRules.Key(trip, date));
    }
}
