using RentNearBy.Core.Models.Owner;
using RentNearBy.Infrastructure.Services.Owner.Be1;
using RentNearBy.Infrastructure.Services.Owner.Be2;
using RentNearBy.Infrastructure.Services.Owner.Be3;
using RentNearBy.Infrastructure.Services.Owner.Be4;
using RentNearBy.Tests.Buddy.Foundation;

namespace RentNearBy.Tests.Owner.Review;

// Pins the fixes of REVIEW_PERF.md and REVIEW_SAFETY.md (owner roles, 2026-11-22). Always run, no database.
public class BackendReviewFixTests
{
    private const string Cte = "pv (\"PlanId\") AS (VALUES ('00000000-0000-0000-0000-000000000000'::uuid))";

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([BuddySources.Root(), .. parts]));

    private static string Method(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        var next = source.IndexOf("    public static async Task<IResult> ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    // PERF B8: the first page has no row comparison, a later page a plain one; the "@cd IS NULL OR" form is gone.
    [Fact]
    public void Calendar_keyset_pages_use_a_plain_row_comparison()
    {
        Assert.DoesNotContain("@cd", OwnerCalendarSql.Upcoming);
        Assert.DoesNotContain("@cd", OwnerCalendarSql.Past);
        Assert.Contains("(c.\"DateFrom\", c.\"Id\") > (@cd, @cid)", OwnerCalendarSql.UpcomingAfter);
        Assert.Contains("(c.\"DateTo\", c.\"Id\") < (@cd, @cid)", OwnerCalendarSql.PastBefore);
        foreach (var sql in new[] { OwnerCalendarSql.Upcoming, OwnerCalendarSql.UpcomingAfter, OwnerCalendarSql.Past, OwnerCalendarSql.PastBefore })
            Assert.DoesNotContain("IS NULL OR", sql);
    }

    // PERF B3: the work numbers read only open or recent enquiries; change requests count on confirmed bookings only.
    [Fact]
    public void Stay_home_work_numbers_never_read_the_whole_history()
    {
        var sql = StayOwnerHomeReader.WorkSql;
        // FB-60: confirmed rows only while the check-out is ahead; the change requests are one CTE (read once).
        Assert.Contains("e.\"Status\" IN ('Asked', 'Countered', 'Offered')", sql);
        Assert.Contains("OR (e.\"Status\" = 'ConfirmedByBoth' AND e.\"CheckOut\" >= @today)", sql);
        Assert.Contains("OR e.\"CreatedAt\" >= now() - interval '7 days'", sql);
        // One change-request read (the CTE) plus the newest guest accept.
        Assert.Equal(2,System.Text.RegularExpressions.Regex.Matches(sql, "q\\.\"Status\" = 'ConfirmedByBoth'").Count);
    }

    // PERF B4: one count per status so each partial index can serve it.
    [Fact]
    public void Taxi_home_live_offers_count_each_status_alone()
    {
        var sql = TaxiDriverHomeReader.WorkSql;
        Assert.DoesNotContain("IN ('Ringing', 'Selected')", sql);
        Assert.Contains("o.\"Status\" = 'Ringing')", sql);
        Assert.Contains("o.\"Status\" = 'Selected')", sql);
    }

    // PERF B5: the route suggestion key moves with the visible route count.
    [Fact]
    public void Taxi_route_suggestion_key_holds_the_route_count()
    {
        var id = Guid.NewGuid();
        Assert.NotEqual(TaxiDriverHomeReader.SuggestionKey(id, 1), TaxiDriverHomeReader.SuggestionKey(id, 2));
        Assert.Equal(TimeSpan.FromMinutes(30), TaxiDriverHomeReader.SuggestionTtl);
    }

    // PERF B9: one pass per child table, the room renumber only for listings whose rooms differ, one EXISTS per offer status.
    [Fact]
    public void Reconcile_page_counts_once_per_table_and_flags_room_changes()
    {
        var stay = PlanLimitsReconcile.BatchSql(ListingKinds.Stay, Cte);
        Assert.Contains("count(*) FILTER (WHERE NOT p.\"HiddenByPlan\") AS photos_vis", stay);
        Assert.Contains("count(*) FILTER (WHERE NOT r.\"HiddenByPlan\") AS rooms_vis", stay);
        Assert.Contains("AS \"RoomsDiffer\"", stay);
        var taxi = PlanLimitsReconcile.BatchSql(ListingKinds.Taxi, Cte);
        Assert.DoesNotContain("IN ('Ringing', 'Accepted')", taxi);
        Assert.Contains("o.\"Status\" = 'Ringing')", taxi);
        Assert.Contains("o.\"Status\" = 'Accepted')", taxi);
        Assert.Contains("rows.Where(r => r.RoomsDiffer)", Source("RentNearBy.Infrastructure", "Services", "Owner", "Be3", "PlanLimitsReconcile.cs"));
    }

    // PERF B10: only a state of the district list keeps its own cache key.
    [Theory]
    [InlineData("Uttarakhand", "Uttarakhand")]
    [InlineData("  Uttarakhand ", "Uttarakhand")]
    [InlineData("Atlantis", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Festival_state_name_must_be_a_known_state(string? asked, string? expected)
        => Assert.Equal(expected, FestivalCache.ResolveState(new HashSet<string>(StringComparer.Ordinal) { "Uttarakhand", "Himachal Pradesh" }, asked));

    // PERF B13: the Home festival comes from the F1 cache; candidates are inside the lead window, soonest first.
    [Fact]
    public void Home_festival_candidates_are_inside_the_lead_window_soonest_first()
    {
        var today = new DateOnly(2026, 11, 22);
        var near = new FestivalCache.Row(Guid.NewGuid(), "Near", today.AddDays(3), today.AddDays(4), null);
        var nearer = new FestivalCache.Row(Guid.NewGuid(), "Nearer", today.AddDays(1), today.AddDays(1), "Uttarakhand");
        var past = new FestivalCache.Row(Guid.NewGuid(), "Past", today.AddDays(-2), today.AddDays(1), null);
        var far = new FestivalCache.Row(Guid.NewGuid(), "Far", today.AddDays(OwnerLimits.FestivalStayLeadDays + 1), today.AddDays(OwnerLimits.FestivalStayLeadDays + 2), null);
        var list = OwnerHomeShared.Candidates([near, far, past, nearer], ListingKinds.Stay, today);
        Assert.Equal(["Nearer", "Near"], list.Select(f => f.Name));
        Assert.Contains("ORDER BY f.ord", OwnerHomeShared.FirstFreeFestivalSql);
    }

    // PERF B13: one statement deletes the festival notices of a saved rule.
    [Fact]
    public void Festival_notices_are_cleared_in_one_statement()
        => Assert.Contains("\"TripId\" = ANY(@entities)", OwnerNotifier.ClearManySql);

    // SAFETY B3: photo files go after the commit (best effort); the stay's jobs close inside the transaction.
    [Fact]
    public void Stay_and_taxi_delete_keep_photo_files_until_the_commit()
    {
        // FB-09: owner and admin delete share ListingRemoval (ListingRemovalJourneyTests pins that both call it).
        var stay = Method(Source("RentNearBy.Api", "Handlers", "Owner", "ListingRemoval.cs"), "public static async Task<ListingRemovalResult> SoftDeleteStayAsync(");
        var commit = stay.IndexOf("CommitTransactionAsync", StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteRoomPhotosAsync", stay);
        Assert.True(stay.IndexOf("DeleteListingPhotosBestEffortAsync", StringComparison.Ordinal) > commit);
        Assert.True(stay.IndexOf("CloseActiveForStayAsync", StringComparison.Ordinal) is var jobs && jobs > 0 && jobs < commit);

        var taxi = Method(Source("RentNearBy.Api", "Handlers", "Owner", "ListingRemoval.cs"), "public static async Task<ListingRemovalResult> SoftDeleteTaxiAsync(");
        Assert.DoesNotContain("DeleteRoomPhotosAsync", taxi);
        Assert.True(taxi.IndexOf("DeleteListingPhotosBestEffortAsync", StringComparison.Ordinal) > taxi.IndexOf("CommitTransactionAsync", StringComparison.Ordinal));
    }

    // SAFETY B2: the calendar create takes the Users row (KEY SHARE) before the listing; deletion retries a deadlock once.
    [Fact]
    public void Calendar_create_locks_users_before_the_listing_and_deletion_retries_a_deadlock()
    {
        var writer = Source("RentNearBy.Infrastructure", "Services", "Owner", "Be1", "OwnerCalendarWriter.cs");
        var share = writer.IndexOf("OwnerLocks.KeyShareUserAsync", StringComparison.Ordinal);
        Assert.True(share > 0 && share < writer.IndexOf("OwnerLocks.LockListingAsync", StringComparison.Ordinal));
        Assert.Contains("FOR KEY SHARE", Source("RentNearBy.Infrastructure", "Services", "Owner", "OwnerLocks.cs"));

        var deletion = Source("RentNearBy.Infrastructure", "Services", "AccountDeletionService.cs");
        Assert.Contains("when (!_committed && TaxiRequests.PgRetry.IsRetryable(ex))", deletion);
        Assert.Contains("CreateAsyncScope()", deletion);
    }
}
