using System.Text;
using RentNearBy.Infrastructure.Repositories;

namespace RentNearBy.Tests.Listings;

public class BrowseCursorTests
{
    private static readonly Guid Id = Guid.Parse("5a5a5a5a-1111-2222-3333-444444444444");
    private static readonly DateTime CreatedAt = Dates.Utc(2026, 10, 20, 8, 15, 30);

    private static string Raw(string raw)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Phases(IReadOnlyList<short> ranks, bool hasCity)
        => string.Join(",", BrowseCursor.Phases(ranks, hasCity).Select(p => $"{p.Rank}{(p.CityOnly ? "c" : "")}"));

    [Fact]
    public void Phases_WithCity_CityThenRestPerRankThenCatchAll()
        => Assert.Equal("2c,2,0c,0,-1", Phases([2, 0], true));

    [Fact]
    public void Phases_WithoutCity_OnePhasePerRankThenCatchAll()
        => Assert.Equal("2,0,-1", Phases([2, 0], false));

    [Fact]
    public void Phases_UnsortedWithDuplicates_Normalised()
        => Assert.Equal("5,2,0,-1", Phases([0, 5, 2, 5, 0], false));

    [Fact]
    public void Phases_NoRanks_CatchAllOnly()
        => Assert.Equal("-1", Phases([], true));

    [Theory]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void EncodeTryDecode_RoundTrips(int rank, bool cityOnly)
    {
        var cursor = BrowseCursor.Encode(new BrowsePhase((short)rank, cityOnly), CreatedAt, Id);

        Assert.True(BrowseCursor.TryDecode(cursor, out var phase, out var at, out var id));
        Assert.Equal((short)rank, phase.Rank);
        Assert.Equal(cityOnly, phase.CityOnly);
        Assert.Equal(CreatedAt, at);
        Assert.Equal(DateTimeKind.Utc, at.Kind);
        Assert.Equal(Id, id);
    }

    [Fact]
    public void Encode_IsUrlSafe()
        => Assert.Matches("^[A-Za-z0-9_-]+$", BrowseCursor.Encode(new BrowsePhase(32767, true), DateTime.MaxValue, Id));

    public static TheoryData<string?> BadCursors => new()
    {
        null,
        "",
        new string('A', 161),
        "%%%not-base64%%%",
        Raw($"2|{CreatedAt.Ticks}|{Id:N}"),
        Raw($"v1|2|1|{CreatedAt.Ticks}|{Id:N}"),
        Raw($"v2|-2|0|{CreatedAt.Ticks}|{Id:N}"),
        Raw($"v2|x|0|{CreatedAt.Ticks}|{Id:N}"),
        Raw($"v2|2|2|{CreatedAt.Ticks}|{Id:N}"),
        Raw($"v2|2|1|-5|{Id:N}"),
        Raw($"v2|2|1|{CreatedAt.Ticks}|not-a-guid"),
        Raw($"v2|2|1|{CreatedAt.Ticks}|{Id:D}"),
        Raw($"v2|2|1|{CreatedAt.Ticks}|{Id:N}|extra"),
    };

    [Theory]
    [MemberData(nameof(BadCursors))]
    public void TryDecode_Malformed_False(string? cursor)
    {
        Assert.False(BrowseCursor.TryDecode(cursor, out _, out var at, out var id));
        Assert.Equal(default(DateTime), at);
        Assert.Equal(Guid.Empty, id);
    }

    [Fact]
    public void Resume_NoCursor_StartsAtZero()
    {
        var (start, afterAt, afterId) = BrowseCursor.Resume(null, BrowseCursor.Phases([2, 0], true));

        Assert.Equal(0, start);
        Assert.Null(afterAt);
        Assert.Equal(Guid.Empty, afterId);
    }

    [Fact]
    public void Resume_PhaseNoLongerInCatalog_Restarts()
    {
        var cursor = BrowseCursor.Encode(new BrowsePhase(7, false), CreatedAt, Id);

        var (start, afterAt, afterId) = BrowseCursor.Resume(cursor, BrowseCursor.Phases([2, 0], true));

        Assert.Equal(0, start);
        Assert.Null(afterAt);
        Assert.Equal(Guid.Empty, afterId);
    }

    [Fact]
    public void Resume_KnownPhase_ItsIndexAndPosition()
    {
        var cursor = BrowseCursor.Encode(new BrowsePhase(0, true), CreatedAt, Id);

        var (start, afterAt, afterId) = BrowseCursor.Resume(cursor, BrowseCursor.Phases([2, 0], true));

        Assert.Equal(2, start);
        Assert.Equal(CreatedAt, afterAt);
        Assert.Equal(Id, afterId);
    }

    private sealed record Row(Guid Id, DateTime CreatedAt);

    private static List<Row> Rows(int count)
        => Enumerable.Range(0, count).Select(i => new Row(Guid.NewGuid(), CreatedAt.AddMinutes(-i))).ToList();

    [Fact]
    public void OffsetPage_MoreThanPage_TrimsAndHasMoreWithoutCursor()
    {
        var page = BrowseCursor.OffsetPage(Rows(11), 10);

        Assert.Equal(10, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void OffsetPage_ExactPage_NoMore()
    {
        var page = BrowseCursor.OffsetPage(Rows(10), 10);

        Assert.Equal(10, page.Items.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void KeysetPage_ExactPage_NoMoreNoCursor()
    {
        var rows = Rows(10);
        var phases = rows.Select(_ => new BrowsePhase(2, true)).ToList();

        var page = BrowseCursor.KeysetPage(rows, phases, 10, r => r.CreatedAt, r => r.Id);

        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
        Assert.Equal(10, page.Items.Count);
    }

    [Fact]
    public void KeysetPage_MoreThanPage_CursorPointsAtLastKeptRow()
    {
        var rows = Rows(11);
        var phases = rows.Select((_, i) => new BrowsePhase((short)(i < 5 ? 2 : 0), false)).ToList();
        var lastKept = rows[9];

        var page = BrowseCursor.KeysetPage(rows, phases, 10, r => r.CreatedAt, r => r.Id);

        Assert.True(page.HasMore);
        Assert.Equal(10, page.Items.Count);
        Assert.True(BrowseCursor.TryDecode(page.NextCursor, out var phase, out var at, out var id));
        Assert.Equal((short)0, phase.Rank);
        Assert.False(phase.CityOnly);
        Assert.Equal(lastKept.CreatedAt, at);
        Assert.Equal(lastKept.Id, id);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(5, false)]
    [InlineData(4, true)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(-1, true)]
    public void OutsideRanks_GappedRanks_MatchesOnlyMissingRanks(int rank, bool expected)
    {
        var predicate = BrowseCursor.OutsideRanks<Row>(_ => (short)rank, [5, 2, 0]).Compile();

        Assert.Equal(expected, predicate(Rows(1)[0]));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(-1, true)]
    public void OutsideRanks_AdjacentRanks_NoGapClause(int rank, bool expected)
    {
        var predicate = BrowseCursor.OutsideRanks<Row>(_ => (short)rank, [1, 0, 1]).Compile();

        Assert.Equal(expected, predicate(Rows(1)[0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32767)]
    [InlineData(-1)]
    public void OutsideRanks_NoRanks_AlwaysTrue(int rank)
    {
        var predicate = BrowseCursor.OutsideRanks<Row>(_ => (short)rank, []).Compile();

        Assert.True(predicate(Rows(1)[0]));
    }
}
