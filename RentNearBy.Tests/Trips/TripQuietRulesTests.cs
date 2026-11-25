namespace RentNearBy.Tests.Trips;

public class TripQuietRulesTests
{
    private static readonly DateTime Asked = new(2026, 11, 25, 4, 0, 0, DateTimeKind.Utc);

    private static TripEnquiry Row(string status = "Asked", string kind = "Stay", double lifeHours = 72) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, Status = status, ListingName = "Pine Nest",
        CreatedAt = Asked, ExpiresAt = Asked.AddHours(lifeHours), UpdatedAt = Asked,
    };

    [Theory]
    [InlineData(72, 12)]   // normal ask: 12 h
    [InlineData(24, 12)]   // half of 24 h = 12 h
    [InlineData(10, 5)]    // short notice: half its life
    [InlineData(3, 2)]     // never under the 2 h floor
    [InlineData(2, 2)]
    public void QuietAt_is_half_the_life_between_2_and_12_hours(double lifeHours, double quietAfter)
        => Assert.Equal(Asked.AddHours(quietAfter), TripQuietRules.QuietAt(Asked, Asked.AddHours(lifeHours)));

    [Fact]
    public void Quiet_starts_exactly_at_QuietAt()
    {
        var e = Row();
        Assert.False(TripQuietRules.IsQuiet(e, Asked.AddHours(12).AddTicks(-1)));
        Assert.True(TripQuietRules.IsQuiet(e, Asked.AddHours(12)));
    }

    [Theory]
    [InlineData("Offered")]
    [InlineData("Countered")]
    [InlineData("ConfirmedByBoth")]
    [InlineData("NotAvailable")]
    [InlineData("Expired")]
    public void Only_an_asked_row_is_quiet(string status)
        => Assert.False(TripQuietRules.IsQuiet(Row(status), Asked.AddDays(2)));

    [Fact]
    public void A_taxi_row_is_never_quiet()
        => Assert.False(TripQuietRules.IsQuiet(Row(kind: "Taxi"), Asked.AddDays(2)));

    [Fact]
    public void An_extended_window_moves_QuietAt_but_never_past_12_hours()
    {
        var e = Row(lifeHours: 6);
        Assert.True(TripQuietRules.IsQuiet(e, Asked.AddHours(3)));
        e.ExpiresAt = Asked.AddHours(30);
        Assert.False(TripQuietRules.IsQuiet(e, Asked.AddHours(3)));
        Assert.True(TripQuietRules.IsQuiet(e, Asked.AddHours(12)));
    }

    [Fact]
    public void Sql_twin_uses_the_same_numbers()
    {
        Assert.Contains("interval '12 hours'", TripQuietRules.QuietAtSql);
        Assert.Contains("interval '2 hours'", TripQuietRules.QuietAtSql);
        Assert.Equal(12, TripQuietRules.QuietAfterHours);
        Assert.Equal(2, TripQuietRules.QuietFloorHours);
    }

    [Fact]
    public void Night_cap_counts_quiet_rows_of_other_stays_apart()
    {
        var night = new DateOnly(2026, 12, 10);
        TripEnquiry On(bool quiet) => new()
        {
            Id = Guid.NewGuid(), Kind = "Stay", Status = "Asked", ListingId = Guid.NewGuid(), ListingName = "x",
            CheckIn = night, CheckOut = night.AddDays(1), CreatedAt = quiet ? Asked.AddHours(-20) : Asked, ExpiresAt = Asked.AddDays(3),
        };
        var me = Guid.NewGuid();
        var threeQuiet = new[] { On(true), On(true), On(true) };
        Assert.False(TripExecutionRules.FitsNightCap(threeQuiet, me, 0, night, night.AddDays(1)));
        Assert.True(TripExecutionRules.FitsNightCap(threeQuiet, me, 0, night, night.AddDays(1), Asked));
        Assert.True(TripExecutionRules.FitsNightCap(threeQuiet, me, 2, night, night.AddDays(1), Asked));
        Assert.False(TripExecutionRules.FitsNightCap(threeQuiet, me, 3, night, night.AddDays(1), Asked));
        var mixed = new[] { On(false), On(false), On(false), On(true) };
        Assert.False(TripExecutionRules.FitsNightCap(mixed, me, 0, night, night.AddDays(1), Asked));
    }
}
