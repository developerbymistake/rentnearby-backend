using RentNearBy.Api.Handlers;

namespace RentNearBy.Tests.Trips;

// Final verification round, owner BB: findings 16-21 (taxi local need, seat cover as the only source, server-priced partial
// stretches, direct multi-driver cover, party quote up to 100 guests).
public class TripFinalVerifyBbTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid District = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // ---- 16: GET /taxis/browse need ----

    [Theory]
    [InlineData(-1, null, null, null)]
    [InlineData(13, null, null, null)]
    [InlineData(null, -5, null, null)]
    [InlineData(null, 1001, null, null)]
    [InlineData(null, null, 29.38, null)]
    [InlineData(null, null, null, 79.46)]
    [InlineData(null, null, 91.0, 79.46)]
    [InlineData(null, null, 29.38, 181.0)]
    public void Need_params_out_of_range_are_rejected(int? people, int? dayKm, double? lat, double? lng)
        => Assert.NotNull(TaxiLocalNeedRules.Error(people, dayKm, lat, lng));

    [Fact]
    public void Need_params_in_range_pass_and_nothing_asked_keeps_the_plain_order()
    {
        Assert.Null(TaxiLocalNeedRules.Error(7, 142, 29.38, 79.46));
        Assert.Null(TaxiLocalNeedRules.Error(null, null, null, null));
        Assert.Null(TaxiLocalNeedRules.Build(null, null, null, null));
        Assert.Null(TaxiLocalNeedRules.Build(0, 0, null, null));
        Assert.Equal(new TaxiLocalNeed(7, 142, 29.38, 79.46), TaxiLocalNeedRules.Build(7, 142, 29.38, 79.46));
    }

    [Fact]
    public void Seven_people_142_km_rank_the_seven_seater_first_and_price_two_small_cars()
    {
        var small = new TaxiLocalCandidate(A, 4, 2500, 100, 0, true, null, null);
        var big = new TaxiLocalCandidate(B, 7, 3500, 200, 0, true, null, null);

        var ranked = TaxiLocalRanking.Rank([small, big], TaxiLocalNeedRules.Build(7, 142, null, null)!);

        Assert.Equal(B, ranked[0].Taxi.Id);
        var a = ranked[1].Fit;
        Assert.Equal((false, 2, 5000, 42), (a.SeatsFit, a.Cars, a.DayCost, a.ExtraKm));
    }

    [Fact]
    public void Local_key_keeps_the_plain_key_without_a_need_and_keys_the_start_point_exactly()
    {
        var plain = TaxiCache.LocalKey(District, null, null, 10, false, true);
        Assert.Equal(plain, TaxiCache.LocalKey(District, null, null, 10, false, true, null));
        Assert.Equal(plain, TaxiCache.LocalKey(District, null, null, 10, false, true, TaxiLocalNeedRules.Build(0, 0, null, null)));

        var here = TaxiCache.LocalKey(District, null, null, 10, false, true, new TaxiLocalNeed(7, 142, 29.38049, 79.46049));
        var there = TaxiCache.LocalKey(District, null, null, 10, false, true, new TaxiLocalNeed(7, 142, 29.38051, 79.46049));
        Assert.StartsWith($"taxis:local:{District:N}:", here);
        Assert.NotEqual(plain, here);
        Assert.NotEqual(here, there);
    }

    [Fact]
    public void Route_search_key_never_rounds_two_callers_into_one()
        => Assert.NotEqual(
            TaxiCache.SearchKey(null, 29.38049, 79.46049, 29.21, 79.51, 10),
            TaxiCache.SearchKey(null, 29.37951, 79.45951, 29.21, 79.51, 10));

    // ---- 17: the seat cover is the only source ----

    private static LegTaxiFare Fare(Guid id, short? seats, int perCar, bool partial = false) => new(id, Guid.NewGuid(), seats, perCar, partial);

    [Fact]
    public void Ten_people_save_the_accepted_cover_not_one_driver_times_three()
    {
        var q = JourneyLegPricing.Quote(Fare(A, 4, 6000), [Fare(C, 7, 9000)], 10);

        Assert.True(q.Covered);
        Assert.Equal((15000, 2), (q.Fare, q.Cars));
        Assert.Equal(new[] { A, C }, q.TaxiIds);
        Assert.Equal(new[] { C }, q.CoverOthers);
        Assert.Equal(2, JourneyLegPricing.CarsOfLeg(10, 4, q.CoverOthers.ToList()));
    }

    [Fact]
    public void An_old_app_without_cover_keeps_one_driver_per_started_group()
    {
        var q = JourneyLegPricing.Quote(Fare(A, 4, 6000), [], 10);

        Assert.False(q.Covered);
        Assert.Equal((18000, 3), (q.Fare, q.Cars));
        Assert.Empty(q.CoverOthers);
        Assert.Equal(3, JourneyLegPricing.CarsOfLeg(10, 4, []));
    }

    [Fact]
    public void A_driver_who_seats_the_group_alone_drops_the_cover()
    {
        var q = JourneyLegPricing.Quote(Fare(C, 12, 9000), [Fare(A, 4, 6000)], 10);

        Assert.Equal((9000, 1), (q.Fare, q.Cars));
        Assert.Empty(q.CoverOthers);
    }

    [Fact]
    public void Duplicate_and_driver_ids_in_the_cover_are_ignored()
    {
        var q = JourneyLegPricing.Quote(Fare(A, 4, 6000), [Fare(A, 4, 6000), Fare(C, 7, 9000), Fare(C, 7, 9000)], 10);

        Assert.Equal(new[] { A, C }, q.TaxiIds);
        Assert.Equal(15000, q.Fare);
    }

    [Fact]
    public void Lead_cars_one_per_covered_car_else_the_drivers_own_count()
    {
        IReadOnlyCollection<Guid> cover = [C];
        Assert.Equal(1, JourneyLegPricing.CarsFor(A, 4, 10, A, cover));
        Assert.Equal(1, JourneyLegPricing.CarsFor(C, 7, 10, A, cover));
        Assert.Equal(3, JourneyLegPricing.CarsFor(B, 4, 10, A, cover));
        Assert.Equal(3, JourneyLegPricing.CarsFor(A, 4, 10, A, []));
        Assert.Equal(6000, JourneyLegPricing.LeadFare(6000, 1));
        Assert.Equal(18000, JourneyLegPricing.LeadFare(6000, 3));
    }

    [Fact]
    public void SolveWith_always_holds_the_required_driver()
    {
        // Without A, B + C (5,000 + 1,000) would seat 10 for less; A is the chosen driver, so A + B is the cover.
        var r = TaxiSeatCover.SolveWith(new SeatOption(0, 4, 6000), [new SeatOption(1, 7, 5000), new SeatOption(2, 4, 1000)], 10);

        Assert.Contains(0, r.Chosen);
        Assert.Equal(new[] { 0, 1 }, r.Chosen);
        Assert.Equal((11000, 2, true), (r.Fare, r.Cars, r.Covered));
    }

    [Fact]
    public void SolveWith_edges_unknown_seats_no_people_and_no_fill()
    {
        var unknown = TaxiSeatCover.SolveWith(new SeatOption(0, null, 5000), [new SeatOption(1, 7, 1000)], 6);
        Assert.Equal((5000, 1, false), (unknown.Fare, unknown.Cars, unknown.Covered));

        var nobody = TaxiSeatCover.SolveWith(new SeatOption(0, 4, 5000), [], 0);
        Assert.Equal((5000, 1, true), (nobody.Fare, nobody.Cars, nobody.Covered));

        var short1 = TaxiSeatCover.SolveWith(new SeatOption(0, 4, 5000), [new SeatOption(1, 2, 1000)], 10);
        Assert.Equal((15000, 3, false), (short1.Fare, short1.Cars, short1.Covered));
    }

    // ---- 18 / 20: a partial stretch is priced from the driver's route on the server ----

    private static readonly TaxiWaysPoint RouteStart = new(29.0, 79.0);
    private static readonly TaxiWaysPoint RouteEnd = new(29.0, 80.0);
    private static readonly List<TaxiWaysPoint> Line = Enumerable.Range(0, 11).Select(i => new TaxiWaysPoint(29.0, 79.0 + i * 0.1)).ToList();

    [Fact]
    public void An_end_4_5_km_off_the_road_but_near_the_route_start_prices_the_stretch_not_the_whole_route()
    {
        var kathgodam = new TaxiWaysPoint(29.0 + 4.5 / 111.2, 79.0);
        var almora = new TaxiWaysPoint(29.0 + 1.0 / 111.2, 79.4);
        Assert.True(TaxiWaysGeo.DistanceMeters(kathgodam, RouteStart) is > 3000 and <= 5000);

        var span = TaxiFare.SpanOnRoute(Line, RouteStart, RouteEnd, kathgodam, almora, 5000, 3000);

        Assert.NotNull(span);
        Assert.Equal(0.40, span!.Value, 2);
        Assert.Equal(3200, TaxiFare.ForSpan(8000, span.Value));
    }

    [Fact]
    public void A_reversed_road_line_gives_the_same_span()
    {
        var almora = new TaxiWaysPoint(29.0, 79.4);
        var reversed = Enumerable.Reverse(Line).ToList();

        Assert.Equal(
            TaxiFare.SpanOnRoute(Line, RouteStart, RouteEnd, RouteStart, almora, 5000, 3000)!.Value,
            TaxiFare.SpanOnRoute(reversed, RouteStart, RouteEnd, RouteStart, almora, 5000, 3000)!.Value, 6);
    }

    [Fact]
    public void An_end_off_the_road_and_far_from_both_ends_is_not_on_the_route()
    {
        var far = new TaxiWaysPoint(29.0 + 10 / 111.2, 79.5);
        Assert.Null(TaxiFare.SpanOnRoute(Line, RouteStart, RouteEnd, RouteStart, far, 5000, 3000));
        Assert.Null(TaxiFare.SpanOnRoute(Line, RouteStart, RouteEnd, RouteStart, RouteStart, 5000, 3000));
        Assert.Null(TaxiFare.SpanOnRoute([], RouteStart, RouteEnd, RouteStart, new TaxiWaysPoint(29.0, 79.5), 5000, 3000));
    }

    [Fact]
    public void Both_ends_near_the_route_ends_are_the_whole_route()
    {
        var span = TaxiFare.SpanOnRoute(Line, RouteStart, RouteEnd, new TaxiWaysPoint(29.02, 79.0), new TaxiWaysPoint(29.0, 79.98), 5000, 3000);
        Assert.Equal(1.0, span!.Value, 9);
        Assert.Equal(8000, TaxiFare.ForSpan(8000, span.Value));
    }

    // ---- 19: a direct stretch gets a multi-driver cover plan ----

    [Fact]
    public void Ten_people_direct_get_a_one_stretch_plan_of_a_seven_and_a_four_seater()
    {
        var s = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 9000, seats: 7),
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 6000, seats: 4));

        var r = TaxiWaysFixture.Search(s, TaxiWaysFixture.Delhi, TaxiWaysFixture.Haridwar, people: 10);

        Assert.Equal(2, r.AllTheWay.Count);
        var plan = r.Plans[0];
        Assert.Single(plan.Legs);
        Assert.Equal(15000, plan.Total);
        Assert.Equal(2, plan.Legs[0].CarsNeeded);
        Assert.Equal(2, plan.Legs[0].CoverDrivers.Select(c => c.Driver.TaxiId).Distinct().Count());
        Assert.True(TaxiWaysKeys.TryParsePlanKey(plan.PlanKey, out var keys));
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public void No_direct_cover_plan_when_one_driver_seats_the_group_or_it_is_not_cheaper()
    {
        var fits = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 9000, seats: 12),
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 6000, seats: 4));
        Assert.Empty(TaxiWaysFixture.Search(fits, TaxiWaysFixture.Delhi, TaxiWaysFixture.Haridwar, people: 10).Plans);

        // 3 x 4,000 = 12,000 for one driver beats 7 + 4 seats at 9,000 + 4,000 = 13,000.
        var dearer = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 9000, seats: 7),
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 4000, seats: 4));
        Assert.Empty(TaxiWaysFixture.Search(dearer, TaxiWaysFixture.Delhi, TaxiWaysFixture.Haridwar, people: 10).Plans);

        var single = TaxiWaysFixture.Build(
            TaxiWaysFixture.Row("Delhi", TaxiWaysFixture.Delhi, "Haridwar", TaxiWaysFixture.Haridwar, 6000, seats: 4));
        Assert.Empty(TaxiWaysFixture.Search(single, TaxiWaysFixture.Delhi, TaxiWaysFixture.Haridwar, people: 10).Plans);
    }

    // ---- 21: the party quote seats up to MaxGuests ----

    [Fact]
    public void Max_guests_is_tied_to_the_rooms_limit()
        => Assert.Equal(StayPartyFit.MaxRoomsWanted * StayPartyFit.MaxRoomCapacity, StayPartyFit.MaxGuests);

    [Theory]
    [InlineData(81, 21)]
    [InlineData(90, 23)]
    [InlineData(100, 25)]
    public void Parties_of_81_to_100_fit_four_plus_rooms(int guests, int rooms)
    {
        var q = StayPartyFit.Quote([new StayFitRoom("Family", 4, 2000, 0)], guests, null, false);

        Assert.True(q.Fits);
        var line = Assert.Single(q.Lines);
        Assert.Equal((rooms, guests), (line.Units, line.Guests));
        Assert.Equal(rooms * 2000, q.PerNight);
    }

    [Fact]
    public void Ninety_guests_in_three_seat_rooms_need_more_than_the_room_limit()
        => Assert.False(StayPartyFit.Quote([new StayFitRoom("Triple", 3, 1500, 0)], 90, null, false).Fits);
}
