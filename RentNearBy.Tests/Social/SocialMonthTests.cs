using RentNearBy.Core.Models.Social;

namespace RentNearBy.Tests.Social;

// Contract 4: every monthly cap is a calendar month in IST.
public class SocialMonthTests
{
    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Month_start_is_the_first_day_00_00_ist_as_utc()
        => Assert.Equal(Utc(2026, 10, 31, 18, 30), SocialMonth.StartUtc(Utc(2026, 11, 15, 6)));

    [Fact]
    public void Late_evening_utc_on_the_last_day_is_already_the_next_ist_month()
        => Assert.Equal(Utc(2026, 11, 30, 18, 30), SocialMonth.StartUtc(Utc(2026, 11, 30, 19)));

    [Fact]
    public void Resets_at_the_next_month_start()
        => Assert.Equal(Utc(2026, 11, 30, 18, 30), SocialMonth.ResetsAtUtc(Utc(2026, 11, 15, 6)));

    [Fact]
    public void December_resets_into_january()
        => Assert.Equal(Utc(2026, 12, 31, 18, 30), SocialMonth.ResetsAtUtc(Utc(2026, 12, 10, 0)));

    [Fact]
    public void Key_is_yyyymm() => Assert.Equal(202612, SocialMonth.Key(new DateOnly(2026, 12, 5)));
}
