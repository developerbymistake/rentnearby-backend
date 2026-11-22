using RentNearBy.Core.Models.Owner;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// FB-27 / JT-B-SK-3: far check-in dates on public stay reads answer 400 with a fixed line.
public class StayDateRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    [Theory]
    [InlineData(9999, 12, 31)]
    [InlineData(1, 1, 1)]
    [InlineData(2027, 11, 23)]
    [InlineData(2026, 11, 20)]
    public void Out_of_range_check_in_is_refused(int y, int m, int d)
        => Assert.Equal(StayDateRules.CheckInOutOfRange, StayDateRules.CheckInError(new DateOnly(y, m, d), Today));

    [Theory]
    [InlineData(2026, 11, 21)]
    [InlineData(2026, 11, 22)]
    [InlineData(2027, 11, 22)]
    public void In_range_or_missing_check_in_passes(int y, int m, int d)
    {
        Assert.Null(StayDateRules.CheckInError(new DateOnly(y, m, d), Today));
        Assert.Null(StayDateRules.CheckInError(null, Today));
    }

    [Theory]
    [InlineData("public static async Task<IResult> GetNearby(")]
    [InlineData("public static async Task<IResult> GetNearest(")]
    [InlineData("public static async Task<IResult> GetBySlug(")]
    [InlineData("public static async Task<IResult> GetById(")]
    public void Every_public_dated_read_checks_the_date(string signature)
    {
        var src = File.ReadAllText(Path.Combine(CompletionCounterGuardTests.Root(), "RentNearBy.Api", "Handlers", "StayHandlers.cs"));
        var body = src[src.IndexOf(signature, StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("CheckInError(checkIn)", body);
    }
}
