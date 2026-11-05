namespace RentNearBy.Tests.Paging;

public class KeysetCursorTests
{
    private static readonly Guid IdA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid IdB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private sealed record Row(DateTime At, Guid Id);

    [Fact]
    public void EncodeDecode_RoundTripsTicksAndId()
    {
        var at = Dates.Utc(2026, 11, 5, 10, 30).AddTicks(1234560);
        Assert.True(KeysetCursor.TryDecode(KeysetCursor.Encode(at, IdA), out var back, out var id));
        Assert.Equal(at, back);
        Assert.Equal(DateTimeKind.Utc, back.Kind);
        Assert.Equal(IdA, id);
    }

    [Fact]
    public void Encode_IsUrlSafe()
    {
        var cursor = KeysetCursor.Encode(Dates.Now, IdB);
        Assert.DoesNotContain('+', cursor);
        Assert.DoesNotContain('/', cursor);
        Assert.DoesNotContain('=', cursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("djJ8MXwy")]
    public void TryDecode_Malformed_IsFalse(string? cursor)
        => Assert.False(KeysetCursor.TryDecode(cursor, out _, out _));

    [Fact]
    public void TryDecode_TooLong_IsFalse()
        => Assert.False(KeysetCursor.TryDecode(new string('a', 500), out _, out _));

    [Fact]
    public void EncodeLast_NoMore_IsNull()
        => Assert.Null(KeysetCursor.EncodeLast(new[] { new Row(Dates.Now, IdA) }, false, r => r.At, r => r.Id));

    [Fact]
    public void EncodeLast_Empty_IsNull()
        => Assert.Null(KeysetCursor.EncodeLast(Array.Empty<Row>(), true, r => r.At, r => r.Id));

    [Fact]
    public void EncodeLast_PointsAtLastRow()
    {
        var rows = new[] { new Row(Dates.Now, IdB), new Row(Dates.Now, IdA) };
        Assert.True(KeysetCursor.TryDecode(KeysetCursor.EncodeLast(rows, true, r => r.At, r => r.Id), out var at, out var id));
        Assert.Equal(Dates.Now, at);
        Assert.Equal(IdA, id);
    }

    // The seek rule the repositories run in SQL: (At, Id) strictly after the cursor in (At DESC, Id DESC) order.
    private static List<Row> Page(IEnumerable<Row> all, DateTime? afterAt, Guid? afterId, int size)
        => all.Where(r => afterAt == null || r.At < afterAt || (r.At == afterAt && r.Id.CompareTo(afterId!.Value) < 0))
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Id).Take(size).ToList();

    [Fact]
    public void Seek_NewRowsAtHeadBetweenPages_NoRepeat()
    {
        var rows = Enumerable.Range(1, 45).Select(i => new Row(Dates.Now.AddMinutes(-i), Guid.NewGuid())).ToList();
        var page1 = Page(rows, null, null, 20);
        rows.Add(new Row(Dates.Now.AddMinutes(1), Guid.NewGuid()));
        rows.Add(new Row(Dates.Now.AddMinutes(2), Guid.NewGuid()));
        var page2 = Page(rows, page1[^1].At, page1[^1].Id, 20);

        Assert.Empty(page1.Intersect(page2));
        Assert.Equal(rows.OrderByDescending(r => r.At).Skip(2).Skip(20).First(), page2[0]);
    }

    [Fact]
    public void Seek_RowsRemovedFromPageOne_NoneSkipped()
    {
        var rows = Enumerable.Range(1, 45).Select(i => new Row(Dates.Now.AddMinutes(-i), Guid.NewGuid())).ToList();
        var page1 = Page(rows, null, null, 20);
        rows.RemoveAll(r => page1.Take(5).Contains(r));
        var page2 = Page(rows, page1[^1].At, page1[^1].Id, 20);

        Assert.Equal(20, page2.Count);
        Assert.Equal(Dates.Now.AddMinutes(-21), page2[0].At);
    }

    [Fact]
    public void Seek_TiedTimestamps_SplitByIdWithoutLossOrRepeat()
    {
        var at = Dates.Now;
        var rows = Enumerable.Range(0, 7).Select(_ => new Row(at, Guid.NewGuid())).ToList();
        var seen = new List<Row>();
        DateTime? afterAt = null;
        Guid? afterId = null;
        while (true)
        {
            var page = Page(rows, afterAt, afterId, 3);
            if (page.Count == 0) break;
            seen.AddRange(page);
            afterAt = page[^1].At;
            afterId = page[^1].Id;
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }
}
