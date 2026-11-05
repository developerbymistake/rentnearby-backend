namespace RentNearBy.Tests.Plans;

public class MoneyTests
{
    [Theory]
    [InlineData(149_500_000L, 14_950L)]
    [InlineData(149_000_000L, 14_900L)]
    [InlineData(1L, 0L)]
    [InlineData(5_000L, 1L)]
    [InlineData(0L, 0L)]
    public void PaiseFromMicros_IsExactForInr(long micros, long expected)
        => Assert.Equal(expected, Money.PaiseFromMicros(micros));

    [Theory]
    [InlineData(149_500_000L, 150)]
    [InlineData(149_499_999L, 149)]
    [InlineData(149_000_000L, 149)]
    public void RupeesFromMicros_RoundsOnce(long micros, int expected)
        => Assert.Equal(expected, Money.RupeesFromMicros(micros));

    [Theory]
    [InlineData(14_950L, 150)]
    [InlineData(14_949L, 149)]
    [InlineData(0L, 0)]
    public void RupeesFromPaise_HalfAwayFromZero(long paise, int expected)
        => Assert.Equal(expected, Money.RupeesFromPaise(paise));

    [Fact]
    public void ThousandRenewalsAt149Point50_SumToExactRupees()
    {
        var paise = Enumerable.Range(0, 1000).Sum(_ => Money.PaiseFromMicros(149_500_000L));
        Assert.Equal(149_500, Money.RupeesFromPaise(paise));
    }

    [Theory]
    [InlineData(149_500_000L, "INR", 14_950L)]
    [InlineData(149_500_000L, "USD", null)]
    [InlineData(null, "INR", null)]
    [InlineData(0L, "INR", null)]
    public void InrPaiseOf_OnlyForInr(long? micros, string? currency, long? expected)
        => Assert.Equal(expected, Money.InrPaiseOf(micros, currency));

    [Theory]
    [InlineData(14_950L, 150, 14_950L)]
    [InlineData(null, 150, 15_000L)]
    public void ChargedPaise_PrefersCapturedPaise(long? captured, int payable, long expected)
        => Assert.Equal(expected, Money.ChargedPaise(captured, payable));
}
