namespace RentNearBy.Tests.TaxiAvailability;

// Contract 1.4 / R10: absent = today, past = today, today + 90 ok, today + 91 or malformed = InvalidDate.
public class TaxiSearchDateTests
{
    private static readonly DateOnly Today = new(2026, 11, 26);

    [Fact]
    public void Absent_or_empty_is_today()
    {
        Assert.Equal(new TaxiSearchDay(true, Today), TaxiSearchDate.Resolve(null, Today));
        Assert.Equal(new TaxiSearchDay(true, Today), TaxiSearchDate.Resolve("", Today));
    }

    [Fact]
    public void A_past_date_reads_as_today()
        => Assert.Equal(new TaxiSearchDay(true, Today), TaxiSearchDate.Resolve("2026-11-20", Today));

    [Fact]
    public void Today_plus_90_is_allowed_and_plus_91_is_refused()
    {
        Assert.Equal(new TaxiSearchDay(true, Today.AddDays(90)), TaxiSearchDate.Resolve(TaxiSearchDate.Text(Today.AddDays(90)), Today));
        Assert.False(TaxiSearchDate.Resolve(TaxiSearchDate.Text(Today.AddDays(91)), Today).Ok);
        Assert.Equal(90, TaxiLimits.SearchDaysAhead);
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("27-11-2026")]
    [InlineData("2026-11-27T00:00:00")]
    [InlineData(" 2026-11-27")]
    [InlineData("2026-1-7")]
    [InlineData("tomorrow")]
    public void Malformed_dates_are_refused(string text) => Assert.False(TaxiSearchDate.Resolve(text, Today).Ok);

    [Fact]
    public void Invalid_date_type_and_message_are_the_contract_strings()
    {
        Assert.Equal("InvalidDate", TaxiSearchDate.InvalidDateType);
        Assert.Equal("Pick a date up to 90 days ahead.", TaxiSearchDate.InvalidDateMessage);
    }

    [Fact]
    public void Key_part_is_compact_and_text_is_iso()
    {
        Assert.Equal(":d20261127", TaxiSearchDate.KeyPart(new DateOnly(2026, 11, 27)));
        Assert.Equal("2026-11-27", TaxiSearchDate.Text(new DateOnly(2026, 11, 27)));
    }
}
