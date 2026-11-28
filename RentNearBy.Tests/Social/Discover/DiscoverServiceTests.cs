using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social.Discover;

// The pure parts of DiscoverService (DESIGN 2, 3.3): paging and cursor cut, the sameTrip source, snapshot assembly with the
// privacy rule, the trip header, live flags, reason fields and the Similar ranking.
public class DiscoverServiceTests
{
    private static Guid Id(int seed)
    {
        var bytes = new byte[16];
        new Random(seed).NextBytes(bytes);
        return new Guid(bytes);
    }

    private static DiscoverSnapshotRow Row(Guid id, DiscoverFlags flags, int points)
        => new(id, DiscoverRules.ZScore(points, id), new DiscoverRowCode(flags, points, 0, null, null, null, [], []));

    private static Func<double?, Task<IReadOnlyList<DiscoverSnapshotRow>>> Reader(IReadOnlyList<DiscoverSnapshotRow> rows)
    {
        var ordered = rows.OrderByDescending(r => r.Score).ToList();
        return bound => Task.FromResult<IReadOnlyList<DiscoverSnapshotRow>>(
            ordered.Where(r => bound is null || r.Score < bound.Value).Take(DiscoverPaging.ReadSize).ToList());
    }

    private static async Task<List<Guid>> PageAllAsync(IReadOnlyList<DiscoverSnapshotRow> rows, string chip, int limit, Func<DiscoverSnapshotRow, bool> survives)
    {
        var seen = new List<Guid>();
        double? start = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var scan = await DiscoverPaging.ScanAsync(Reader(rows), start, chip);
            var survivors = scan.Matched.Where(survives).ToList();
            var (take, cursorRow) = DiscoverPaging.Cut(survivors, scan, limit);
            seen.AddRange(survivors.Take(take).Select(s => s.UserId));
            if (cursorRow is null) return seen;
            var cursor = DiscoverPaging.Cursor(1, cursorRow);
            Assert.True(DiscoverCursor.TryDecode(cursor, out var value));
            start = value.ZScore;
        }
        throw new InvalidOperationException("paging did not end");
    }

    [Fact]
    public async Task Sixty_tied_rows_page_out_once_each()
    {
        var rows = Enumerable.Range(0, 60).Select(i => Row(Id(i), DiscoverFlags.Mutual, 30)).ToList();
        var seen = await PageAllAsync(rows, DiscoverChips.ForYou, 20, _ => true);
        Assert.Equal(60, seen.Count);
        Assert.Equal(60, seen.Distinct().Count());
    }

    [Fact]
    public async Task Unsent_survivors_are_never_skipped_when_the_hydrate_returns_more_than_the_page()
    {
        var rows = Enumerable.Range(0, 200).Select(i => Row(Id(100 + i), DiscoverFlags.Near, i % 7 * 10)).ToList();
        var seen = await PageAllAsync(rows, DiscoverChips.ForYou, 7, _ => true);
        Assert.Equal(rows.Select(r => r.UserId).OrderBy(x => x), seen.OrderBy(x => x));
    }

    [Fact]
    public async Task Rows_dropped_by_the_live_filter_and_other_chips_are_skipped_without_losing_the_rest()
    {
        var rows = Enumerable.Range(0, 200).Select(i => Row(Id(500 + i), i % 2 == 0 ? DiscoverFlags.Mutual : DiscoverFlags.Near, i)).ToList();
        var dropped = rows.Where((_, i) => i % 10 == 0).Select(r => r.UserId).ToHashSet();
        var seen = await PageAllAsync(rows, DiscoverChips.Mutual, 20, r => !dropped.Contains(r.UserId));
        var expected = rows.Where(r => r.Code!.Flags == DiscoverFlags.Mutual && !dropped.Contains(r.UserId)).Select(r => r.UserId);
        Assert.Equal(expected.OrderBy(x => x), seen.OrderBy(x => x));
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task Scan_stops_at_thirty_matches_and_after_four_reads()
    {
        var rows = Enumerable.Range(0, 200).Select(i => Row(Id(900 + i), DiscoverFlags.Near, i)).ToList();
        var scan = await DiscoverPaging.ScanAsync(Reader(rows), null, DiscoverChips.ForYou);
        Assert.Equal(DiscoverPaging.HydrateSize, scan.Matched.Count);
        Assert.False(scan.Exhausted);
        Assert.Equal(scan.Matched[^1], scan.LastExamined);

        var many = Enumerable.Range(0, 300).Select(i => Row(Id(2000 + i), DiscoverFlags.Near, i % 50)).ToList();
        var reads = 0;
        var sparse = await DiscoverPaging.ScanAsync(b => { reads++; return Reader(many)(b); }, null, DiscoverChips.SameTrip);
        Assert.Equal(DiscoverPaging.MaxReads, reads);
        Assert.Empty(sparse.Matched);
        Assert.False(sparse.Exhausted);
    }

    [Fact]
    public async Task An_empty_or_short_snapshot_ends_with_no_cursor()
    {
        var empty = await DiscoverPaging.ScanAsync(Reader([]), null, DiscoverChips.ForYou);
        Assert.True(empty.Exhausted);
        Assert.Null(DiscoverPaging.Cut([], empty, 20).CursorRow);
        var rows = Enumerable.Range(0, 5).Select(i => Row(Id(i), DiscoverFlags.Near, i)).ToList();
        var scan = await DiscoverPaging.ScanAsync(Reader(rows), null, DiscoverChips.ForYou);
        Assert.Equal((5, (DiscoverSnapshotRow?)null), DiscoverPaging.Cut(scan.Matched, scan, 20));
    }

    [Fact]
    public void Trip_candidates_need_the_same_city_and_near_dates_one_row_per_owner()
    {
        var start = new DateOnly(2026, 12, 12);
        var trips = new[] { new DiscoverService.MyTripRow { CityKey = 1, StartDate = start, EndDate = start.AddDays(4) } };
        var a = Id(1); var b = Id(2); var c = Id(3); var d = Id(4);
        var posts = new[]
        {
            new DiscoverService.PostRow { OwnerUserId = a, CityKey = 1, StartDate = start.AddDays(10), PostDays = 2 },
            new DiscoverService.PostRow { OwnerUserId = a, CityKey = 1, StartDate = start, PostDays = 2 },
            new DiscoverService.PostRow { OwnerUserId = b, CityKey = 1, StartDate = start.AddDays(40), PostDays = 2 },
            new DiscoverService.PostRow { OwnerUserId = c, CityKey = 2, StartDate = start, PostDays = 2 },
            new DiscoverService.PostRow { OwnerUserId = d, CityKey = 1, PostStartMonth = 202701 },
        };
        var result = DiscoverService.TripCandidates(trips, posts);
        Assert.Equal(new[] { $"{a}|1|202612", $"{d}|1|202701" }, result.Select(r => $"{r.Id}|{r.City}|{r.Month}"));
    }

    [Fact]
    public void Assembly_keeps_private_rows_only_with_allowed_sources_and_drops_their_city()
    {
        var pub = Id(10); var priv = Id(11); var privNearOnly = Id(12);
        var rows = new[]
        {
            new DiscoverService.CandidateRow { UserId = pub, Flags = (int)(DiscoverFlags.SameTrip | DiscoverFlags.Interests), CityKey = 7, Month = 202612, InterestIdx = [0, 2] },
            new DiscoverService.CandidateRow { UserId = priv, Flags = (int)(DiscoverFlags.SameTrip | DiscoverFlags.Mutual), MutualCount = 3, MutualFirstId = pub, CityKey = 7, Month = 202612, IsPrivate = true },
            new DiscoverService.CandidateRow { UserId = privNearOnly, Flags = (int)DiscoverFlags.Near, IsPrivate = true },
        };
        var built = DiscoverService.Assemble(rows, null, new DateTime(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2, built.Rows.Count);
        var p = built.Rows.Single(r => r.UserId == pub).Code!;
        Assert.Equal((7L, 202612, 100 + 16), (p.CityKey!.Value, p.Month!.Value, p.Points));
        Assert.Equal([0, 2], p.InterestIdx);
        var q = built.Rows.Single(r => r.UserId == priv).Code!;
        Assert.Equal(DiscoverFlags.Mutual, q.Flags);
        Assert.Null(q.CityKey);
        Assert.Null(q.Month);
        Assert.Equal(30, q.Points);
        Assert.All(built.Rows, r => Assert.Equal(DiscoverRules.ZScore(r.Code!.Points, r.UserId), r.Score));
        Assert.Equal(1795824000000, built.Stamp);
    }

    [Fact]
    public void Trip_header_only_when_every_same_trip_row_is_in_my_soonest_city()
    {
        var soonest = new DiscoverService.MyTripRow { CityKey = 7, StartDate = new DateOnly(2026, 12, 12), EndDate = new DateOnly(2026, 12, 16) };
        DiscoverSnapshotRow Same(int seed, long city) => new(Id(seed), 100, new DiscoverRowCode(DiscoverFlags.SameTrip, 100, 0, null, city, 202612, [], []));
        Assert.Equal("7|202612", DiscoverService.TripField([Same(1, 7), Same(2, 7), Row(Id(3), DiscoverFlags.Near, 25)], soonest));
        Assert.Null(DiscoverService.TripField([Same(1, 7), Same(2, 8)], soonest));
        Assert.Null(DiscoverService.TripField([Row(Id(3), DiscoverFlags.Near, 25)], soonest));
        Assert.Null(DiscoverService.TripField([Same(1, 7)], null));
        Assert.Null(DiscoverService.TripField([Same(1, 7)], new DiscoverService.MyTripRow { CityKey = 7 }));
    }

    [Fact]
    public void Live_flags_drop_lost_sources_and_a_new_private_profile()
    {
        var cached = DiscoverFlags.SameTrip | DiscoverFlags.Mutual | DiscoverFlags.FollowsYou | DiscoverFlags.Interests | DiscoverFlags.Places;
        var live = new DiscoverService.LiveRow { MutualCount = 0, FollowsMe = true, SharedInterests = ["Treks"], SharedPlaces = [] };
        Assert.Equal(DiscoverFlags.SameTrip | DiscoverFlags.FollowsYou | DiscoverFlags.Interests, DiscoverService.LiveFlags(cached, live));
        live.IsPrivate = true;
        Assert.Equal(DiscoverFlags.FollowsYou, DiscoverService.LiveFlags(cached, live));
        live.FollowsMe = false;
        Assert.Equal(DiscoverFlags.None, DiscoverService.LiveFlags(cached, live));
    }

    [Fact]
    public void Reasons_carry_only_their_kind_fields_and_private_rows_carry_no_city_month_or_tags()
    {
        var code = new DiscoverRowCode(DiscoverFlags.SameTrip, 100, 0, null, 7, 202612, [], []);
        var same = DiscoverService.Reason(DiscoverReasonKinds.SameTrip, DiscoverFlags.SameTrip, code, false, "Manali", 0, null, [], []);
        Assert.Equal(("Manali", 202612, (string?)null, (IReadOnlyList<string>?)null), (same.CityName, same.Month!.Value, same.MutualName, same.Tags));

        var mate = DiscoverService.Reason(DiscoverReasonKinds.TravelledWith, DiscoverFlags.TravelledWith, code, true, "Manali", 0, null, [], []);
        Assert.Null(mate.CityName);
        Assert.Null(mate.Month);

        var mutual = DiscoverService.Reason(DiscoverReasonKinds.Mutual, DiscoverFlags.Mutual, code, true, "Manali", 4, "Aman Verma", ["Treks"], []);
        Assert.Equal(("Aman", 4), (mutual.MutualName, mutual.MutualCount!.Value));
        Assert.Null(mutual.CityName);
        Assert.Null(mutual.Tags);

        var noName = DiscoverService.Reason(DiscoverReasonKinds.Mutual, DiscoverFlags.Mutual, code, false, null, 2, null, [], []);
        Assert.Null(noName.MutualName);
        Assert.Equal(2, noName.MutualCount);

        var near = DiscoverService.Reason(DiscoverReasonKinds.Near, DiscoverFlags.Near | DiscoverFlags.Interests, code, false, "Manali", 0, null,
            ["Treks", "Food", "Culture", "Camping"], []);
        Assert.Equal(["Treks", "Food", "Culture"], near.Tags!);
        Assert.Null(near.CityName);
        Assert.Null(DiscoverService.Reason(DiscoverReasonKinds.Near, DiscoverFlags.Near, code, false, null, 0, null, ["Treks"], []).Tags);

        var places = DiscoverService.Reason(DiscoverReasonKinds.Places, DiscoverFlags.Places, code, false, null, 0, null, [], ["Manali"]);
        Assert.Equal(["Manali"], places.Tags!);
        Assert.Null(places.Month);
    }

    [Fact]
    public void Similar_ranks_by_points_drops_private_rows_outside_the_following_list_and_takes_the_limit()
    {
        var rows = new[]
        {
            new DiscoverService.SimilarRow { UserId = Id(1), Name = "Kavya", Flags = (int)DiscoverFlags.Mutual, ViaName = "Rahul Sharma" },
            new DiscoverService.SimilarRow { UserId = Id(2), Name = "Vikas", Flags = (int)DiscoverFlags.Interests, Tags = ["Treks", "Food"] },
            new DiscoverService.SimilarRow { UserId = Id(3), Name = "Neha", Flags = (int)DiscoverFlags.SameTrip, CityName = "Manali" },
            new DiscoverService.SimilarRow { UserId = Id(4), Name = "Ravi", Flags = (int)DiscoverFlags.SameTrip, CityName = "Manali", IsPrivate = true },
        };
        var items = DiscoverService.RankSimilar(rows, 5);
        Assert.Equal(["Neha", "Vikas", "Kavya"], items.Select(i => i.Name));
        Assert.Equal("Manali", items[0].Reason.CityName);
        Assert.Equal(["Treks", "Food"], items[1].Reason.Tags!);
        Assert.Equal("Rahul", items[2].Reason.MutualName);
        Assert.Single(DiscoverService.RankSimilar(rows, 1));
    }

    [Fact]
    public void Cache_bound_is_exclusive_and_round_trips_the_exact_score()
    {
        Assert.Equal("+inf", DiscoverCache.MaxArg(null));
        var z = DiscoverRules.ZScore(37, Id(42));
        var arg = DiscoverCache.MaxArg(z);
        Assert.StartsWith("(", arg);
        Assert.Equal(z, double.Parse(arg[1..], System.Globalization.CultureInfo.InvariantCulture));
        var row = new DiscoverSnapshotRow(Id(42), z, null);
        Assert.Equal(37, row.Points);
    }
}
