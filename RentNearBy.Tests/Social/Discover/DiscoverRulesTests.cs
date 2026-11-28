using RentNearBy.Core.Models.Social;

namespace RentNearBy.Tests.Social.Discover;

// Discover ranking, reasons, chips, dates near, unique zscores, snapshot quota and the snapshot row code (DESIGN 1.4, 2, 3.1).
public class DiscoverRulesTests
{
    private static Guid Id(int seed)
    {
        var bytes = new byte[16];
        new Random(seed).NextBytes(bytes);
        return new Guid(bytes);
    }

    [Fact]
    public void Points_follow_the_table()
    {
        Assert.Equal(100, DiscoverRules.Points(DiscoverFlags.SameTrip, 0, 0, 0));
        Assert.Equal(150, DiscoverRules.Points(DiscoverFlags.SameTrip | DiscoverFlags.Mutual, 7, 0, 0));
        Assert.Equal(30, DiscoverRules.Points(DiscoverFlags.Mutual, 3, 0, 0));
        Assert.Equal(24, DiscoverRules.Points(DiscoverFlags.Interests, 0, 5, 0));
        Assert.Equal(10, DiscoverRules.Points(DiscoverFlags.Places, 0, 0, 2));
        Assert.Equal(75, DiscoverRules.Points(DiscoverFlags.TravelledWith, 0, 0, 0, recent: true));
        Assert.Equal(0, DiscoverRules.Points(DiscoverFlags.None, 0, 0, 0, recent: true));
        Assert.Equal(25 + 16, DiscoverRules.Points(DiscoverFlags.Near | DiscoverFlags.Interests, 0, 2, 0));
    }

    [Fact]
    public void Reason_is_the_top_kind_and_ties_follow_the_table_order()
    {
        Assert.Equal(DiscoverReasonKinds.FollowsYou, DiscoverRules.TopReason(DiscoverFlags.FollowsYou | DiscoverFlags.Mutual, 5, 0, 0));
        Assert.Equal(DiscoverReasonKinds.Mutual, DiscoverRules.TopReason(DiscoverFlags.Mutual | DiscoverFlags.Near, 6, 0, 0));
        Assert.Equal(DiscoverReasonKinds.Near, DiscoverRules.TopReason(DiscoverFlags.Near | DiscoverFlags.Interests, 0, 3, 0));
        Assert.Equal(DiscoverReasonKinds.Interests, DiscoverRules.TopReason(DiscoverFlags.Interests | DiscoverFlags.Places, 0, 4, 3));
        Assert.Equal(DiscoverReasonKinds.SameTrip, DiscoverRules.TopReason((DiscoverFlags)127, 5, 3, 3));
        Assert.Null(DiscoverRules.TopReason(DiscoverFlags.None, 0, 0, 0));
    }

    [Fact]
    public void A_chip_page_shows_the_reason_of_its_chip()
    {
        var f = DiscoverFlags.SameTrip | DiscoverFlags.Mutual | DiscoverFlags.Places;
        Assert.Equal(DiscoverReasonKinds.Mutual, DiscoverRules.ReasonFor(DiscoverChips.Mutual, f, 4, 0, 1));
        Assert.Equal(DiscoverReasonKinds.Places, DiscoverRules.ReasonFor(DiscoverChips.Interests, f, 4, 0, 1));
        Assert.Equal(DiscoverReasonKinds.SameTrip, DiscoverRules.ReasonFor(DiscoverChips.ForYou, f, 4, 0, 1));
        Assert.Null(DiscoverRules.ReasonFor(DiscoverChips.Near, f, 4, 0, 1));
    }

    [Fact]
    public void Chips_parse_and_filter()
    {
        Assert.True(DiscoverChips.TryParse(null, out var c) && c == DiscoverChips.ForYou);
        Assert.True(DiscoverChips.TryParse("", out c) && c == DiscoverChips.ForYou);
        Assert.True(DiscoverChips.TryParse("near", out c) && c == DiscoverChips.Near);
        Assert.False(DiscoverChips.TryParse("Near", out _));
        Assert.False(DiscoverChips.TryParse("bogus", out _));
        Assert.True(DiscoverRules.Matches(DiscoverFlags.Places, DiscoverChips.Interests));
        Assert.True(DiscoverRules.Matches(DiscoverFlags.Near, DiscoverChips.ForYou));
        Assert.False(DiscoverRules.Matches(DiscoverFlags.None, DiscoverChips.ForYou));
        Assert.False(DiscoverRules.Matches(DiscoverFlags.Mutual, DiscoverChips.SameTrip));
    }

    [Fact]
    public void A_private_candidate_keeps_only_the_allowed_sources()
    {
        Assert.Equal(DiscoverFlags.None, DiscoverRules.VisibleFlags(DiscoverFlags.SameTrip | DiscoverFlags.Near | DiscoverFlags.Interests, isPrivate: true));
        Assert.Equal(DiscoverFlags.Mutual, DiscoverRules.VisibleFlags(DiscoverFlags.SameTrip | DiscoverFlags.Mutual, isPrivate: true));
        Assert.Equal(DiscoverFlags.SameTrip | DiscoverFlags.Mutual, DiscoverRules.VisibleFlags(DiscoverFlags.SameTrip | DiscoverFlags.Mutual, isPrivate: false));
    }

    [Fact]
    public void Exact_dates_are_near_within_seven_days()
    {
        var mine = DiscoverSpan.Exact(new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 14));
        Assert.True(DiscoverRules.DatesNear(mine, DiscoverSpan.Exact(new DateOnly(2026, 12, 12), new DateOnly(2026, 12, 16))));
        Assert.True(DiscoverRules.DatesNear(mine, DiscoverSpan.Exact(new DateOnly(2026, 12, 21), new DateOnly(2026, 12, 22))));
        Assert.False(DiscoverRules.DatesNear(mine, DiscoverSpan.Exact(new DateOnly(2026, 12, 22), new DateOnly(2026, 12, 23))));
        Assert.True(DiscoverRules.DatesNear(mine, DiscoverSpan.Exact(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 3))));
        Assert.False(DiscoverRules.DatesNear(mine, DiscoverSpan.Exact(new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 2))));
    }

    [Fact]
    public void Months_are_near_when_equal_or_adjacent_across_the_year()
    {
        Assert.True(DiscoverRules.DatesNear(DiscoverSpan.Months(202612, 202612), DiscoverSpan.Months(202701, 202701)));
        Assert.False(DiscoverRules.DatesNear(DiscoverSpan.Months(202612, 202612), DiscoverSpan.Months(202702, 202702)));
        Assert.True(DiscoverRules.DatesNear(DiscoverSpan.Months(202610, 202612), DiscoverSpan.Months(202609, 202609)));
        var exactDec = DiscoverSpan.Exact(new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 14));
        Assert.True(DiscoverRules.DatesNear(exactDec, DiscoverSpan.Months(202701, 202701)));
        Assert.False(DiscoverRules.DatesNear(exactDec, DiscoverSpan.Months(202610, 202610)));
        Assert.True(DiscoverRules.DatesNear(DiscoverSpan.Unknown, DiscoverSpan.Months(202610, 202610)));
        Assert.Equal(202701, DiscoverRules.MonthFromIndex(DiscoverRules.MonthIndex(202612) + 1));
    }

    [Fact]
    public void Post_span_uses_dates_then_month_then_the_window()
    {
        var exact = DiscoverRules.PostSpan(new DateOnly(2026, 12, 10), 4, null, null, null);
        Assert.Equal(new DateOnly(2026, 12, 13), exact.End);
        Assert.Equal(DiscoverSpan.Months(202701, 202701), DiscoverRules.PostSpan(null, 3, 202701, null, null));
        var published = new DateTime(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
        Assert.Equal(DiscoverSpan.Months(202611, 202701), DiscoverRules.PostSpan(null, 3, null, published, 3));
        Assert.Equal(DiscoverSpan.Unknown, DiscoverRules.PostSpan(null, null, null, null, null));
    }

    [Fact]
    public void ZScore_adds_a_forty_bit_tie_below_one()
    {
        var half = Guid.Parse("80000000-0000-0000-0000-000000000000");
        Assert.Equal(10.5, DiscoverRules.ZScore(10, half));
        Assert.Equal(0.0, DiscoverRules.Tie(Guid.Empty));
        Assert.True(DiscoverRules.Tie(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")) < 1.0);
        Assert.True(DiscoverRules.ZScore(5000, half) < DiscoverRules.MaxPoints + 1);
        Assert.Equal(0.5, DiscoverRules.ZScore(-3, half));
    }

    [Fact]
    public void ZScores_of_200_ids_are_unique_and_ordered_by_points_then_tie()
    {
        var rows = Enumerable.Range(0, 200).Select(i => (Points: i % 7 * 10, Id: Id(1000 + i))).ToList();
        var z = rows.Select(r => DiscoverRules.ZScore(r.Points, r.Id)).ToList();
        Assert.Equal(200, z.Distinct().Count());
        var byZ = rows.OrderByDescending(r => DiscoverRules.ZScore(r.Points, r.Id)).Select(r => r.Id).ToList();
        var byPair = rows.OrderByDescending(r => r.Points).ThenByDescending(r => DiscoverRules.Tie(r.Id)).Select(r => r.Id).ToList();
        Assert.Equal(byPair, byZ);
    }

    [Fact]
    public void Three_pages_over_sixty_tied_rows_return_every_row_once()
    {
        var snapshot = Enumerable.Range(0, 60).Select(i => Id(5000 + i))
            .OrderByDescending(id => DiscoverRules.ZScore(40, id)).ToList();
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            double? bound = null;
            if (cursor != null)
            {
                Assert.True(DiscoverCursor.TryDecode(cursor, out var c));
                bound = c.ZScore;
            }
            var page = snapshot.Where(id => bound == null || DiscoverRules.ZScore(40, id) < bound).Take(20).ToList();
            seen.AddRange(page);
            pages++;
            cursor = page.Count == 20 && seen.Count < snapshot.Count ? DiscoverCursor.Encode(1, 40, page[^1]) : null;
        } while (cursor != null);
        Assert.Equal(3, pages);
        Assert.Equal(60, seen.Count);
        Assert.Equal(60, seen.Distinct().Count());
    }

    [Fact]
    public void Snapshot_keeps_a_quota_for_every_chip()
    {
        var candidates = Enumerable.Range(0, 300).Select(i => new DiscoverCandidate(Id(i), DiscoverFlags.SameTrip, 100))
            .Concat(Enumerable.Range(300, 50).Select(i => new DiscoverCandidate(Id(i), DiscoverFlags.Near, 25)))
            .ToList();
        var snap = DiscoverRules.SelectSnapshot(candidates);
        Assert.Equal(200, snap.Count);
        Assert.Equal(40, snap.Count(c => c.Flags == DiscoverFlags.Near));
        Assert.Equal(160, snap.Count(c => c.Flags == DiscoverFlags.SameTrip));
        Assert.Equal(snap.OrderByDescending(c => DiscoverRules.ZScore(c.Points, c.UserId)).ToList(), snap);
    }

    [Fact]
    public void Snapshot_dedupes_and_stays_under_the_max()
    {
        var a = Id(1);
        var snap = DiscoverRules.SelectSnapshot([new(a, DiscoverFlags.Mutual, 20), new(a, DiscoverFlags.Mutual, 30), new(Id(2), DiscoverFlags.Near, 25)]);
        Assert.Equal(2, snap.Count);
        Assert.Equal(30, snap.Single(c => c.UserId == a).Points);
    }

    [Fact]
    public void Row_code_round_trips()
    {
        var mutual = Id(7);
        var row = new DiscoverRowCode(DiscoverFlags.SameTrip | DiscoverFlags.Mutual, 140, 4, mutual, 210000123, 202612, [0, 2], [1]);
        var text = row.Encode();
        Assert.Equal($"9|140|4|{mutual:N}|210000123|202612|02|1", text);
        Assert.True(DiscoverRowCode.TryDecode(text, out var back));
        Assert.Equal(row.Flags, back.Flags);
        Assert.Equal(row.Points, back.Points);
        Assert.Equal(row.MutualFirstId, back.MutualFirstId);
        Assert.Equal(row.CityKey, back.CityKey);
        Assert.Equal(row.Month, back.Month);
        Assert.Equal(row.InterestIdx, back.InterestIdx);
        Assert.Equal(row.PlaceIdx, back.PlaceIdx);

        var empty = new DiscoverRowCode(DiscoverFlags.Near, 25, 0, null, null, null, [], []);
        Assert.Equal("16|25|0|||||", empty.Encode());
        Assert.True(DiscoverRowCode.TryDecode(empty.Encode(), out var e2));
        Assert.Null(e2.MutualFirstId);
        Assert.Null(e2.CityKey);
        Assert.Null(e2.Month);

        Assert.False(DiscoverRowCode.TryDecode(null, out _));
        Assert.False(DiscoverRowCode.TryDecode("1|2|3", out _));
        Assert.False(DiscoverRowCode.TryDecode("x|1|0|||||", out _));
        Assert.False(DiscoverRowCode.TryDecode("1|1|0|nope||||", out _));
    }

    [Fact]
    public void Snapshot_keys_share_one_hash_tag()
    {
        var u = Guid.Parse("0123456789abcdef0123456789abcdef");
        Assert.Equal(
            ["discover:{0123456789abcdef0123456789abcdef}:z", "discover:{0123456789abcdef0123456789abcdef}:r", "discover:{0123456789abcdef0123456789abcdef}:s"],
            DiscoverKeys.Snapshot(u));
        Assert.Equal("discover:lock:{0123456789abcdef0123456789abcdef}", DiscoverKeys.Lock(u));
        Assert.Equal("discover:rf:{0123456789abcdef0123456789abcdef}", DiscoverKeys.Refresh(u));
        Assert.Equal(21_600_000L, DiscoverKeys.SnapshotTtlMs);
    }
}
