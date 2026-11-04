namespace RentNearBy.Tests.Trips;

public class TaxiWaysRankingTests
{
    private static TaxiWaysCandidate Cand(int fare, short seats, short rank = 0, string label = "new", bool partial = false, bool basedHere = false)
    {
        var driver = new TaxiWaysDriver { TaxiId = Guid.NewGuid(), Seats = seats, PriorityRank = rank, TripReplyLabel = label };
        var link = new TaxiWaysLink { RouteId = Guid.NewGuid(), TaxiId = driver.TaxiId, FromPlace = "a", ToPlace = "b", Fare = fare, Partial = partial };
        return new TaxiWaysCandidate(driver, link, false, basedHere);
    }

    private static List<TaxiWaysCandidate> Sorted(IComparer<TaxiWaysCandidate> cmp, params TaxiWaysCandidate[] items)
    {
        var list = items.ToList();
        list.Sort(cmp);
        return list;
    }

    [Fact]
    public void A_car_with_enough_seats_comes_before_a_small_one()
    {
        var small = Cand(5000, 4);
        var big = Cand(9000, 7);

        var list = Sorted(TaxiWaysRanking.Compare(6), small, big);

        Assert.Same(big, list[0]);
    }

    [Fact]
    public void Among_cars_that_fit_a_whole_route_comes_before_part_of_a_route()
    {
        var part = Cand(3000, 6, partial: true);
        var whole = Cand(9000, 6);

        var list = Sorted(TaxiWaysRanking.Compare(4), part, whole);

        Assert.Same(whole, list[0]);
    }

    [Fact]
    public void Higher_plan_rank_wins_when_everything_else_is_equal()
    {
        var plain = Cand(5000, 6, rank: 0);
        var ranked = Cand(5000, 6, rank: 2);

        var list = Sorted(TaxiWaysRanking.Compare(4), plain, ranked);

        Assert.Same(ranked, list[0]);
    }

    [Fact]
    public void Lower_fare_wins_as_the_last_tiebreaker()
    {
        var dear = Cand(9000, 6);
        var cheap = Cand(8000, 6);

        var list = Sorted(TaxiWaysRanking.Compare(4), dear, cheap);

        Assert.Same(cheap, list[0]);
    }

    [Fact]
    public void Lowest_price_sorts_by_fare_first()
    {
        var cheap = Cand(5000, 4);
        var dear = Cand(9000, 7);

        var list = Sorted(TaxiWaysRanking.LowestPrice(6), dear, cheap);

        Assert.Same(cheap, list[0]);
    }

    [Fact]
    public void Based_here_drivers_come_first_when_asked()
    {
        var far = Cand(5000, 6, rank: 3);
        var here = Cand(7000, 6, basedHere: true);

        var list = Sorted(TaxiWaysRanking.Compare(4, basedHereFirst: true), far, here);

        Assert.Same(here, list[0]);
    }

    [Fact]
    public void Seats_fit_needs_a_known_seat_count()
    {
        var unknown = new TaxiWaysDriver { TaxiId = Guid.NewGuid(), Seats = null };
        var enough = new TaxiWaysDriver { TaxiId = Guid.NewGuid(), Seats = 6 };

        Assert.False(TaxiWaysRanking.SeatsFit(unknown, 4));
        Assert.True(TaxiWaysRanking.SeatsFit(enough, 6));
        Assert.False(TaxiWaysRanking.SeatsFit(enough, 7));
    }

    [Fact]
    public void Sorting_is_stable_for_identical_candidates()
    {
        var a = Cand(5000, 6);
        var b = Cand(5000, 6);

        var first = Sorted(TaxiWaysRanking.Compare(4), a, b);
        var second = Sorted(TaxiWaysRanking.Compare(4), b, a);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
    }
}
