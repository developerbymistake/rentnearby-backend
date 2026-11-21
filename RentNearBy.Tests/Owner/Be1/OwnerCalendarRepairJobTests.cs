using RentNearBy.Infrastructure.Services.Owner.Be1;

namespace RentNearBy.Tests.Owner.Be1;

// REVIEW_PERF B2: the repair job walks each set by id from a cursor, so a source it cannot write never starves the rest.
public class OwnerCalendarRepairJobTests
{
    public static TheoryData<string> Statements() => new()
    {
        OwnerCalendarRepairJob.MissingEnquiriesSql,
        OwnerCalendarRepairJob.StaleEnquiriesSql,
        OwnerCalendarRepairJob.MissingBookingsSql,
        OwnerCalendarRepairJob.StaleBookingsSql,
    };

    [Theory]
    [MemberData(nameof(Statements))]
    public void Every_repair_select_is_ordered_and_starts_after_the_cursor(string sql)
    {
        Assert.Contains("> @after", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.True(sql.IndexOf("ORDER BY", StringComparison.Ordinal) < sql.IndexOf("LIMIT @n", StringComparison.Ordinal));
    }

    [Fact]
    public void A_full_page_moves_the_cursor_to_its_last_id()
    {
        var page = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(page[^1], OwnerCalendarRepairJob.NextCursor(page, 3));
    }

    [Fact]
    public void A_short_page_starts_the_next_run_from_the_beginning()
    {
        Assert.Equal(Guid.Empty, OwnerCalendarRepairJob.NextCursor([Guid.NewGuid()], 3));
        Assert.Equal(Guid.Empty, OwnerCalendarRepairJob.NextCursor([], 3));
    }
}
