using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Trips;

// ALGORITHM round, owner BB: findings 15, 16, 19, 20, 81, 87.
public class TripMoneyAlgoTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateTime Now = new(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc);

    private static TripOfferLine Line(string room, int units, int guests, int price) =>
        new() { RoomName = room, Units = units, Guests = guests, PricePerNight = price };

    // ---------- MoneySplit.Apportion ----------

    [Fact]
    public void Apportion_sums_exactly_and_gives_leftovers_to_the_largest_fractions()
    {
        var shares = MoneySplit.Apportion(100, [1, 1, 1]);
        Assert.Equal(100, shares.Sum());
        Assert.Equal(new[] { 34, 33, 33 }, shares);
    }

    [Fact]
    public void Apportion_edge_cases()
    {
        Assert.Empty(MoneySplit.Apportion(100, []));
        Assert.Equal(new[] { 0, 0 }, MoneySplit.Apportion(0, [3, 4]));
        Assert.Equal(new[] { 7 }, MoneySplit.Apportion(7, [5]));
        Assert.Equal(new[] { 4, 3 }, MoneySplit.Apportion(7, [0, 0]));
        Assert.Equal(new[] { 0, 7 }, MoneySplit.Apportion(7, [0, 9]));
    }

    [Fact]
    public void Apportion_with_caps_moves_the_overflow()
    {
        var shares = MoneySplit.Apportion(6, [1, 1], [2, 10]);
        Assert.Equal(new[] { 2, 4 }, shares);
        Assert.Equal(new[] { 3, 5 }, MoneySplit.Apportion(8, [1, 1], [2, 4]));
    }

    [Theory]
    [InlineData(7500, 1, 3, 2500)]
    [InlineData(1001, 1, 2, 501)]
    [InlineData(1000, 3, 3, 1000)]
    [InlineData(1000, 0, 3, 0)]
    [InlineData(1000, 1, 0, 0)]
    public void ProRata_rounds_half_up(int amount, int part, int whole, int expected)
        => Assert.Equal(expected, MoneySplit.ProRata(amount, part, whole));

    // ---------- Findings 20 and 81: counter lines keep the exact total ----------

    [Fact]
    public void Counter_finding_20_total_is_price_times_rooms_times_nights()
    {
        var lines = TripOfferRules.CounterLines([Line("Std", 1, 2, 1000), Line("Dlx", 2, 4, 1500)], 1100, 3, 6);
        Assert.Equal(3300, lines.Sum(l => l.Units * l.PricePerNight));
        Assert.Equal(16500, TripExecutionRules.ComputeTotal(lines, 5));
        Assert.Equal(826, lines[0].PricePerNight);
        Assert.Equal(1237, lines[1].PricePerNight);
    }

    [Fact]
    public void Counter_finding_81_total_is_18000()
    {
        var lines = TripOfferRules.CounterLines([Line("Dlx", 1, 2, 3000), Line("Std", 2, 4, 2000)], 2000, 3, 6);
        Assert.Equal(6000, lines.Sum(l => l.Units * l.PricePerNight));
        Assert.Equal(18000, TripExecutionRules.ComputeTotal(lines, 3));
        Assert.Equal(2572, lines[0].PricePerNight);
        Assert.Equal(1714, lines[1].PricePerNight);
    }

    [Fact]
    public void UnitPrices_needs_a_negative_move_when_units_share_no_one()
    {
        // Floors 999 and 1002 leave 1 rupee over 2 and 3 units: only 3 - 2 (or 4 - 3) reaches it.
        var prices = MoneySplit.UnitPrices(5 * 1001, [2, 3], [1000, 1003]);
        Assert.Equal(new[] { 998, 1003 }, prices);
        Assert.Equal(5005, 2 * prices![0] + 3 * prices[1]);
    }

    [Fact]
    public void UnitPrices_equal_prices_give_the_flat_price()
    {
        Assert.Equal(new[] { 1234, 1234, 1234 }, MoneySplit.UnitPrices(1234L * 6, [1, 2, 3], [900, 900, 900]));
        Assert.Null(MoneySplit.UnitPrices(100, [], []));
        Assert.Null(MoneySplit.UnitPrices(100, [1], [0]));
    }

    [Fact]
    public void Counter_many_mixes_always_hit_the_exact_total()
    {
        var rnd = new Random(7);
        for (var run = 0; run < 80; run++)
        {
            var count = rnd.Next(1, 6);
            var offer = Enumerable.Range(0, count)
                .Select(i => Line($"R{i}", rnd.Next(1, 5), 2, rnd.Next(500, 20000)))
                .ToList();
            var x = rnd.Next(100, 15000);
            var units = offer.Sum(l => l.Units);
            var lines = TripOfferRules.CounterLines(offer, x, units, 2);
            Assert.Equal((long)x * units, lines.Sum(l => (long)l.Units * l.PricePerNight));
            Assert.All(lines, l => Assert.True(l.PricePerNight >= 1));
        }
    }

    // ---------- Finding 19: change lines by capacity ----------

    [Fact]
    public void Change_finding_19_guests_follow_capacity()
    {
        var accepted = new List<TripOfferLine> { Line("Deluxe", 1, 2, 2000), Line("Family", 1, 4, 3500) };
        var lines = TripOfferRules.ChangeLines(accepted, rooms: 2, guests: 5)!;
        Assert.Equal(2, lines[0].Guests);
        Assert.Equal(3, lines[1].Guests);
    }

    [Fact]
    public void Change_finding_19_added_room_goes_where_it_is_cheapest_and_seats_the_party()
    {
        var accepted = new List<TripOfferLine> { Line("Deluxe", 1, 2, 2000), Line("Family", 1, 4, 3500) };
        var seven = TripOfferRules.ChangeLines(accepted, rooms: 3, guests: 7)!;
        Assert.Equal(new[] { 2, 1 }, seven.Select(l => l.Units));
        Assert.Equal(7500, seven.Sum(l => l.Units * l.PricePerNight));
        Assert.Equal(7, seven.Sum(l => l.Guests));
        Assert.All(seven.Zip(new[] { 2, 4 }), x => Assert.True(x.First.Guests <= x.First.Units * x.Second));

        var nine = TripOfferRules.ChangeLines(accepted, rooms: 3, guests: 9)!;
        Assert.Equal(new[] { 1, 2 }, nine.Select(l => l.Units));
    }

    [Fact]
    public void Change_uses_the_stay_room_capacity_when_known()
    {
        var accepted = new List<TripOfferLine> { Line("Family", 1, 2, 3500) };
        Assert.Null(TripOfferRules.ChangeLines(accepted, rooms: 1, guests: 4));
        var caps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["family"] = 4 };
        var lines = TripOfferRules.ChangeLines(accepted, rooms: 1, guests: 4, caps)!;
        Assert.Equal(4, Assert.Single(lines).Guests);
    }

    [Fact]
    public void Change_with_the_same_rooms_keeps_the_host_mix()
    {
        // Two Deluxe would be cheaper for 4 guests, but the accepted mix still seats them.
        var accepted = new List<TripOfferLine> { Line("Deluxe", 1, 2, 2000), Line("Family", 1, 2, 3500) };
        var lines = TripOfferRules.ChangeLines(accepted, rooms: 2, guests: 4, new Dictionary<string, int> { ["Family"] = 4 })!;
        Assert.Equal(new[] { 1, 1 }, lines.Select(l => l.Units));
    }

    [Fact]
    public void SpreadGuests_edge_cases()
    {
        Assert.Empty(MoneySplit.SpreadGuests(3, [], []));
        Assert.Equal(new[] { 0, 0 }, MoneySplit.SpreadGuests(0, [1, 1], [2, 2]));
        Assert.Equal(new[] { 1, 0 }, MoneySplit.SpreadGuests(1, [1, 1], [2, 2]));
        Assert.Equal(new[] { 2, 0, 2 }, MoneySplit.SpreadGuests(4, [1, 0, 1], [2, 4, 2]));
    }

    // ---------- Finding 15: party fit ----------

    private static readonly StayFitRoom Deluxe = new("Deluxe", 2, 1500, 1);
    private static readonly StayFitRoom Family = new("Family", 4, 4500, 0);

    [Fact]
    public void PartyFit_finding_15_six_guests_two_rooms()
    {
        var q = StayPartyFit.Quote([Deluxe, Family], guests: 6, roomsWanted: 2, groundFloorOnly: false, nights: 2);
        Assert.True(q.Fits);
        Assert.Equal(6000, q.PerNight);
        Assert.Equal(12000, q.Total);
        Assert.Equal(new[] { ("Deluxe", 1, 2), ("Family", 1, 4) }, q.Lines.Select(l => (l.RoomName, l.Units, l.Guests)));
    }

    [Fact]
    public void PartyFit_a_stay_too_small_does_not_fit()
    {
        var b = new StayFitRoom("Deluxe", 2, 1200, 1);
        var q = StayPartyFit.Quote([b], guests: 6, roomsWanted: 2, groundFloorOnly: false);
        Assert.False(q.Fits);
        Assert.Empty(q.Lines);
        Assert.Null(q.PerNight);
    }

    [Fact]
    public void PartyFit_four_plus_counts_as_four()
    {
        Assert.False(StayPartyFit.Quote([Family], guests: 10, roomsWanted: 1, groundFloorOnly: false).Fits);
        var any = StayPartyFit.Quote([Family], guests: 10, roomsWanted: null, groundFloorOnly: false);
        Assert.True(any.Fits);
        Assert.Equal(3, Assert.Single(any.Lines).Units);
        Assert.Equal(13500, any.PerNight);
    }

    [Fact]
    public void PartyFit_without_rooms_picks_the_cheapest_count()
    {
        var q = StayPartyFit.Quote([Deluxe, Family], guests: 6, roomsWanted: null, groundFloorOnly: false);
        Assert.Equal(4500, q.PerNight);
        Assert.Equal(3, Assert.Single(q.Lines).Units);
    }

    [Fact]
    public void PartyFit_ground_floor_only_uses_ground_rooms()
    {
        var q = StayPartyFit.Quote([Deluxe, Family], guests: 2, roomsWanted: 1, groundFloorOnly: true);
        Assert.Equal("Family", Assert.Single(q.Lines).RoomName);
        Assert.False(StayPartyFit.Quote([Deluxe], guests: 2, roomsWanted: 1, groundFloorOnly: true).Fits);
    }

    [Fact]
    public void PartyFit_empty_single_and_unpriced()
    {
        Assert.False(StayPartyFit.Quote([], 2, 1, false).Fits);
        var single = StayPartyFit.Quote([Deluxe], 2, 1, false);
        Assert.Equal(1500, single.PerNight);
        var unpriced = StayPartyFit.Quote([new StayFitRoom("A", 3, null, null)], 3, null, false);
        Assert.True(unpriced.Fits);
        Assert.Null(unpriced.PerNight);
        Assert.Equal(1, Assert.Single(unpriced.Lines).Units);
    }

    [Fact]
    public void Nearby_apply_sets_the_quote_and_the_real_best_room()
    {
        var stay = new StayNearbyDto
        {
            Id = Guid.NewGuid(),
            HasGroundFloorRoom = true,
            Rooms =
            {
                new StayNearbyRoomDto { RoomName = "Deluxe", MaxGuests = 2, PricePerNight = 1500, Floor = 1 },
                new StayNearbyRoomDto { RoomName = "Family", MaxGuests = 4, PricePerNight = 4500, Floor = 0 },
            },
        };
        var small = new StayNearbyDto
        {
            Id = Guid.NewGuid(),
            Rooms = { new StayNearbyRoomDto { RoomName = "Deluxe", MaxGuests = 2, PricePerNight = 1200 } },
        };

        var items = StayNearbyRooms.Apply([stay, small], guests: 6, groundFloorOnly: false, roomsWanted: 2, nights: 2);
        Assert.True(items[0].Fits);
        Assert.Equal(6000, items[0].QuotePerNight);
        Assert.Equal(12000, items[0].QuoteTotal);
        Assert.Equal("Family", items[0].BestRoomName);
        Assert.Equal(2, items[0].QuoteLines!.Count);
        Assert.False(items[1].Fits);
        Assert.Null(items[1].QuoteTotal);

        var noParty = StayNearbyRooms.Apply([stay], guests: null, groundFloorOnly: false);
        Assert.Null(noParty[0].Fits);
        Assert.Equal("Family", noParty[0].BestRoomName);
    }

    // ---------- Finding 16: advance against a moving total ----------

    [Fact]
    public void Change_to_a_smaller_total_covers_with_the_paid_advance_and_shows_the_refund()
    {
        Assert.Equal(2500, TripAdvanceSplit.ForTerms(5000, 7500, 2500, TripAdvanceStatuses.Received, 5000));
        var e = new TripEnquiry
        {
            Kind = TripEnquiryKinds.Stay, Status = TripEnquiryStatuses.ConfirmedByBoth,
            AdvanceStatus = TripAdvanceStatuses.Received, AdvanceAmount = 5000, AgreedTotal = 7500,
        };
        Assert.Null(TripAdvanceSplit.RefundDue(e));
        TripAdvanceSplit.ApplyTotalChange(e, 2500);
        e.AgreedTotal = 2500;
        Assert.Equal(5000, e.AdvanceAmount);
        Assert.Equal(2500, TripAdvanceSplit.RefundDue(e));
    }

    [Fact]
    public void Unpaid_advance_follows_the_total_both_ways()
    {
        Assert.Equal(1667, TripAdvanceSplit.ForTerms(5000, 7500, 2500, TripAdvanceStatuses.None, null));
        Assert.Equal(10000, TripAdvanceSplit.ForTerms(5000, 7500, 15000, TripAdvanceStatuses.Pending, 5000));
        Assert.Null(TripAdvanceSplit.ForTerms(null, 7500, 2500, TripAdvanceStatuses.None, null));
        Assert.Equal(0, TripAdvanceSplit.ForTerms(0, 7500, 2500, TripAdvanceStatuses.None, null));

        var e = new TripEnquiry { AdvanceStatus = TripAdvanceStatuses.Pending, AdvanceAmount = 3000, AgreedTotal = 9000 };
        TripAdvanceSplit.ApplyTotalChange(e, 3000);
        Assert.Equal(1000, e.AdvanceAmount);
    }

    [Fact]
    public void Paid_advance_on_a_bigger_total_stays_as_paid()
    {
        Assert.Equal(5000, TripAdvanceSplit.ForTerms(5000, 7500, 10000, TripAdvanceStatuses.Received, 5000));
    }

    [Fact]
    public void Sequential_allocation_three_parts()
    {
        var (parts, refund) = TripAdvanceSplit.Sequential(5000, [2000, 2000, 2000]);
        Assert.Equal(new[] { 2000, 2000, 1000 }, parts);
        Assert.Equal(0, refund);
        var (more, back) = TripAdvanceSplit.Sequential(7000, [2000, 2000]);
        Assert.Equal(new[] { 2000, 2000 }, more);
        Assert.Equal(3000, back);
        var (none, zero) = TripAdvanceSplit.Sequential(0, []);
        Assert.Empty(none);
        Assert.Equal(0, zero);
    }

    private static TripEnquiry Booked(int total, int? advance, string advanceStatus) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        TravellerUserId = Guid.NewGuid(),
        OwnerUserId = Guid.NewGuid(),
        Kind = TripEnquiryKinds.Stay,
        ListingId = Guid.NewGuid(),
        ListingName = "Deodar Homestay",
        Day = 1,
        CheckIn = Start,
        CheckOut = Start.AddDays(3),
        Status = TripEnquiryStatuses.ConfirmedByBoth,
        AcceptedOfferId = Guid.NewGuid(),
        AdvanceStatus = advanceStatus,
        AdvanceAmount = advance,
        AgreedTotal = total,
        ExpiresAt = Now.AddDays(1),
    };

    [Fact]
    public void Problem_split_finding_16_moves_the_unused_advance_to_the_failed_night()
    {
        var e = Booked(7500, 5000, TripAdvanceStatuses.Received);
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);

        Assert.Equal(2500, kept!.AgreedTotal);
        Assert.Equal(2500, kept.AdvanceAmount);
        Assert.Null(TripAdvanceSplit.RefundDue(kept));

        Assert.Equal(5000, failed.AgreedTotal);
        Assert.Equal(TripAdvanceStatuses.Received, failed.AdvanceStatus);
        Assert.Equal(2500, failed.AdvanceAmount);
        Assert.Equal(2500, TripAdvanceSplit.RefundDue(failed));
        Assert.Equal(5000, kept.AdvanceAmount + failed.AdvanceAmount);
    }

    [Fact]
    public void Problem_split_advance_within_the_kept_part_leaves_no_refund()
    {
        var e = Booked(9000, 2000, TripAdvanceStatuses.Received);
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);
        Assert.Equal(2000, kept!.AdvanceAmount);
        Assert.Equal(TripAdvanceStatuses.None, failed.AdvanceStatus);
        Assert.Null(failed.AdvanceAmount);
    }

    [Fact]
    public void Problem_split_unpaid_ask_follows_the_kept_total()
    {
        var e = Booked(9000, 3000, TripAdvanceStatuses.Pending);
        var (kept, failed) = TripProblemSplit.Plan(e, Start, 2, Now);
        Assert.Equal(1000, kept!.AdvanceAmount);
        Assert.Equal(TripAdvanceStatuses.None, failed.AdvanceStatus);
    }

    [Fact]
    public void Shrunk_offer_carries_the_covered_advance()
    {
        var e = Booked(7500, 5000, TripAdvanceStatuses.Received);
        var accepted = new TripOffer { Id = Guid.NewGuid(), Kind = TripOfferKinds.Offer, Total = 7500, AdvanceAmount = 5000, LinesJson = "[]" };
        var (kept, _) = TripProblemSplit.Plan(e, Start, 2, Now);
        Assert.Equal(2500, TripProblemSplit.ShrinkAcceptedOffer(accepted, kept!, Now).AdvanceAmount);
    }

    // ---------- Finding 87: the night cap as a pure rule ----------

    private static TripEnquiry Open(Guid listing, int from, int to) => new()
    {
        Id = Guid.NewGuid(), ListingId = listing, Kind = TripEnquiryKinds.Stay, Status = TripEnquiryStatuses.Asked,
        CheckIn = Start.AddDays(from - 1), CheckOut = Start.AddDays(to),
    };

    [Fact]
    public void Night_cap_counts_other_stays_and_this_request()
    {
        var me = Guid.NewGuid();
        var open = new List<TripEnquiry> { Open(Guid.NewGuid(), 1, 2), Open(Guid.NewGuid(), 2, 2), Open(me, 1, 3) };
        Assert.True(TripExecutionRules.FitsNightCap(open, me, 0, Start.AddDays(1), Start.AddDays(2)));
        Assert.False(TripExecutionRules.FitsNightCap(open, me, 1, Start.AddDays(1), Start.AddDays(2)));
        Assert.True(TripExecutionRules.FitsNightCap(open, me, 1, Start.AddDays(2), Start.AddDays(3)));
        Assert.True(TripExecutionRules.FitsNightCap([], me, 2, Start, Start.AddDays(1)));
        Assert.False(TripExecutionRules.FitsNightCap([], me, 3, Start, Start.AddDays(1)));
    }

    [Fact]
    public void Night_cap_ignores_rows_without_dates()
    {
        var open = new List<TripEnquiry> { new() { ListingId = Guid.NewGuid(), CheckIn = null, CheckOut = null } };
        Assert.True(TripExecutionRules.FitsNightCap(open, Guid.NewGuid(), 2, Start, Start.AddDays(1)));
    }

    // ---------- Finding 84: the trip view of the DTO builder ----------

    [Fact]
    public void Trip_view_has_no_plan_for_another_viewer()
    {
        var view = new TripEnquiryTripView(Guid.NewGuid(), Start, Start.AddDays(2), null, 210000001, "Nainital", 29.38, 79.45, null);
        Assert.Null(view.Plan());
        var own = view with { PlanJson = "{}" };
        Assert.NotNull(own.Plan());
    }
}
