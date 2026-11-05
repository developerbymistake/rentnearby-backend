namespace RentNearBy.Tests.Listings;

public class ListingVisibilityTests
{
    private static readonly DateTime Now = Dates.Utc(2026, 11, 5, 10, 16);

    // Finding 37: the free window ends at 10:16, the row is not public at 10:16 even before the :15 sweep of the next hour.
    [Theory]
    [InlineData(true, false, 0, false)]
    [InlineData(true, false, 1, true)]
    [InlineData(true, false, -59, false)]
    [InlineData(false, false, 60, false)]
    [InlineData(true, true, 60, false)]
    public void IsPublic_ValidUntilIsEnforcedAtReadTime(bool active, bool deleted, int minutesLeft, bool expected)
        => Assert.Equal(expected, ListingVisibility.IsPublic(active, deleted, Now.AddMinutes(minutesLeft), Now));

    [Fact]
    public void IsPublic_NoWindow_Public()
        => Assert.True(ListingVisibility.IsPublic(true, false, null, Now));

    [Fact]
    public void CacheTtl_NoExpiringRow_FullTtl()
        => Assert.Equal(TimeSpan.FromSeconds(60), ListingVisibility.CacheTtl(TimeSpan.FromSeconds(60), Now, [null, null]));

    [Fact]
    public void CacheTtl_Empty_FullTtl()
        => Assert.Equal(TimeSpan.FromMinutes(3), ListingVisibility.CacheTtl(TimeSpan.FromMinutes(3), Now, []));

    [Fact]
    public void CacheTtl_EndsAtTheFirstExpiry()
        => Assert.Equal(TimeSpan.FromSeconds(20),
            ListingVisibility.CacheTtl(TimeSpan.FromSeconds(60), Now, [Now.AddMinutes(5), Now.AddSeconds(20), null]));

    [Fact]
    public void CacheTtl_AlreadyExpiredRow_DoNotCache()
        => Assert.Null(ListingVisibility.CacheTtl(TimeSpan.FromSeconds(60), Now, [Now]));

    [Fact]
    public void PublishedAt_FirstApproval_IsNow()
        => Assert.Equal(Now, ListingPublishRules.PublishedAtOnActivation(Dates.Utc(2026, 10, 1), null, Now));

    // Finding 29: drafted 1 Oct, approved 5 Nov -> newest is 5 Nov, not 1 Oct.
    [Fact]
    public void PublishedAt_RenewalAfterExpiry_IsNow()
        => Assert.Equal(Now, ListingPublishRules.PublishedAtOnActivation(Dates.Utc(2026, 10, 1), Now.AddDays(-3), Now));

    [Fact]
    public void PublishedAt_ResumeInsideRunningWindow_Kept()
    {
        var first = Dates.Utc(2026, 10, 20);
        Assert.Equal(first, ListingPublishRules.PublishedAtOnActivation(first, Now.AddDays(10), Now));
    }

    private sealed record Row(string Name, short Rank, DateTime At, Guid Id);

    private static ListingFeedOrder.Key K(Row r) => new(r.Rank, r.At, r.Id);

    [Fact]
    public void FeedOrder_RankThenNewestThenIdDescending()
    {
        var idLow = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var idHigh = Guid.Parse("ffffffff-0000-0000-0000-000000000001");
        var at = Dates.Utc(2026, 11, 1);
        Assert.True(ListingFeedOrder.Compare(new(5, at.AddDays(-90), idLow), new(0, at, idLow)) < 0);
        Assert.True(ListingFeedOrder.Compare(new(0, at, idLow), new(0, at.AddDays(-1), idLow)) < 0);
        Assert.True(ListingFeedOrder.Compare(new(0, at, idHigh), new(0, at, idLow)) < 0);
        Assert.Equal(0, ListingFeedOrder.Compare(new(0, at, idLow), new(0, at, idLow)));
    }

    // Finding 30: rooms are truncated by (rank, publishedAt); the merge must use the same order, so a paid
    // August room is not re-sorted below a newer free plot, and the newest free rows keep their place among free rows.
    [Fact]
    public void MergeTop_KWayMergeEqualsSortOfTheUnion()
    {
        var at = Dates.Utc(2026, 11, 5, 10, 0);
        var rooms = new List<Row>
        {
            new("paidRoomAug", 5, Dates.Utc(2026, 8, 10), Guid.NewGuid()),
            new("freeRoomNow", 0, at, Guid.NewGuid()),
            new("freeRoomSep", 0, Dates.Utc(2026, 9, 1), Guid.NewGuid()),
        };
        var plots = new List<Row>
        {
            new("freePlot9am", 0, at.AddHours(-1), Guid.NewGuid()),
            new("freePlotOct", 0, Dates.Utc(2026, 10, 1), Guid.NewGuid()),
        };
        var stays = new List<Row>();

        var merged = ListingFeedOrder.MergeTop<Row>([rooms, plots, stays], K, 4);

        Assert.Equal(new[] { "paidRoomAug", "freeRoomNow", "freePlot9am", "freePlotOct" }, merged.Select(r => r.Name));
        var union = rooms.Concat(plots).ToList();
        union.Sort((a, b) => ListingFeedOrder.Compare(K(a), K(b)));
        Assert.Equal(union.Take(4), merged);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void MergeTop_NonPositiveTake_Empty(int take)
        => Assert.Empty(ListingFeedOrder.MergeTop<Row>([[new Row("a", 0, Now, Guid.NewGuid())]], K, take));

    [Fact]
    public void MergeTop_AllEmpty_Empty()
        => Assert.Empty(ListingFeedOrder.MergeTop<Row>([[], []], K, 5));

    [Fact]
    public void MergeTop_TakeLargerThanUnion_ReturnsAll()
        => Assert.Equal(2, ListingFeedOrder.MergeTop<Row>([[new Row("a", 0, Now, Guid.NewGuid())], [new Row("b", 0, Now, Guid.NewGuid())]], K, 10).Count);
}
