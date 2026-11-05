using System.Text;
using RentNearBy.Infrastructure.Repositories;

namespace RentNearBy.Tests.Listings;

public class BrowseCursorV3Tests
{
    private static readonly Guid Id = Guid.Parse("5a5a5a5a-1111-2222-3333-444444444444");
    private static readonly DateTime PublishedAt = Dates.Utc(2026, 11, 5, 10, 0, 0);

    private static string Raw(string raw)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Newest_RoundTripsPublishedAt()
    {
        var cursor = BrowseCursor.Encode(BrowseSorts.Newest, new BrowsePhase(2, true), BrowseCursor.NewestKey(PublishedAt), Id);

        Assert.True(BrowseCursor.TryDecode(cursor, BrowseSorts.Newest, out var phase, out var key, out var id));
        Assert.Equal(new BrowsePhase(2, true), phase);
        Assert.Equal(PublishedAt, BrowseCursor.NewestAt(key));
        Assert.Equal(DateTimeKind.Utc, BrowseCursor.NewestAt(key).Kind);
        Assert.Equal(Id, id);
    }

    [Theory]
    [InlineData(BrowseSorts.PriceAsc, "3000")]
    [InlineData(BrowseSorts.PriceDesc, "0")]
    [InlineData(BrowseSorts.AreaAsc, "6804.00")]
    [InlineData(BrowseSorts.AreaDesc, "27225.5")]
    public void ValueSorts_RoundTripExactly(string sort, string value)
    {
        var k = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        var cursor = BrowseCursor.Encode(sort, new BrowsePhase(BrowseCursor.OtherRanks, true), k, Id);

        Assert.True(BrowseCursor.TryDecode(cursor, sort, out var phase, out var key, out _));
        Assert.Equal(new BrowsePhase(-1, true), phase);
        Assert.Equal(k, key);
    }

    [Fact]
    public void Cursor_OfAnotherSort_RestartsAtFirstPage()
    {
        var cursor = BrowseCursor.Encode(BrowseSorts.PriceAsc, new BrowsePhase(0, false), 5000m, Id);
        var phases = BrowseCursor.Phases([2, 0], false);

        var (start, afterKey, afterId) = BrowseCursor.Resume(cursor, BrowseSorts.Newest, phases);

        Assert.Equal(0, start);
        Assert.Null(afterKey);
        Assert.Equal(Guid.Empty, afterId);
    }

    public static TheoryData<string?, string> BadCursors => new()
    {
        { null, BrowseSorts.Newest },
        { "", BrowseSorts.Newest },
        { new string('A', 201), BrowseSorts.Newest },
        { "%%%not-base64%%%", BrowseSorts.Newest },
        { Raw($"v2|2|1|{PublishedAt.Ticks}|{Id:N}"), BrowseSorts.Newest },
        { Raw($"v3|newest|2|1|{PublishedAt.Ticks}|{Id:N}|x"), BrowseSorts.Newest },
        { Raw($"v3|newest|-2|1|{PublishedAt.Ticks}|{Id:N}"), BrowseSorts.Newest },
        { Raw($"v3|newest|2|2|{PublishedAt.Ticks}|{Id:N}"), BrowseSorts.Newest },
        { Raw($"v3|newest|2|1|-5|{Id:N}"), BrowseSorts.Newest },
        { Raw($"v3|newest|2|1|1.5|{Id:N}"), BrowseSorts.Newest },
        { Raw($"v3|newest|2|1|{PublishedAt.Ticks}|{Id:D}"), BrowseSorts.Newest },
        { Raw($"v3|price_asc|2|1|{PublishedAt.Ticks}|{Id:N}"), BrowseSorts.PriceAsc },
        { Raw($"v3|price_desc|2|1|10.5|{Id:N}"), BrowseSorts.PriceDesc },
    };

    [Theory]
    [MemberData(nameof(BadCursors))]
    public void Malformed_DecodesFalse(string? cursor, string sort)
    {
        Assert.False(BrowseCursor.TryDecode(cursor, sort, out _, out var key, out var id));
        Assert.Equal(0m, key);
        Assert.Equal(Guid.Empty, id);
    }

    [Fact]
    public void KeysetPage_CursorCarriesSortAndLastRowKey()
    {
        var rows = Enumerable.Range(0, 11).Select(i => (Id: Guid.NewGuid(), Price: 1000 + i * 100)).ToList();
        var phases = rows.Select(_ => new BrowsePhase(0, true)).ToList();

        var page = BrowseCursor.KeysetPage(rows, phases, 10, BrowseSorts.PriceAsc, r => r.Price, r => r.Id);

        Assert.True(page.HasMore);
        Assert.True(BrowseCursor.TryDecode(page.NextCursor, BrowseSorts.PriceAsc, out _, out var key, out var id));
        Assert.Equal(1900m, key);
        Assert.Equal(rows[9].Id, id);
    }

    // Finding 35: ranks {3, 0}, a city row still at rank 2 (before the reconcile) must fall in exactly one phase.
    [Fact]
    public void Phases_DriftedRankCityRow_HasAPhase()
    {
        var phases = BrowseCursor.Phases([3, 0], true);
        Assert.Equal(1, phases.Count(p => Matches(p, [3, 0], rank: 2, inCity: true)));
    }

    // Phases partition the live set: every (rank, inCity) row matches exactly one phase.
    [Fact]
    public void Phases_PartitionEveryRow()
    {
        var random = new Random(35);
        for (var trial = 0; trial < 200; trial++)
        {
            var ranks = Enumerable.Range(0, random.Next(0, 5)).Select(_ => (short)random.Next(0, 6)).ToList();
            var hasCity = random.Next(2) == 0;
            var phases = BrowseCursor.Phases(ranks, hasCity);
            for (short rank = -1; rank <= 7; rank++)
                foreach (var inCity in new[] { true, false })
                {
                    var hits = phases.Count(p => Matches(p, ranks, rank, hasCity && inCity, hasCity));
                    Assert.Equal(1, hits);
                }
        }
    }

    private static bool Matches(BrowsePhase phase, IReadOnlyList<short> ranks, short rank, bool inCity, bool hasCity = true)
    {
        var rankOk = phase.Rank == BrowseCursor.OtherRanks
            ? BrowseCursor.OutsideRanks<short>(r => r, ranks).Compile()(rank)
            : phase.Rank == rank;
        var cityOk = !hasCity || (phase.CityOnly ? inCity : !inCity);
        return rankOk && cityOk;
    }
}
