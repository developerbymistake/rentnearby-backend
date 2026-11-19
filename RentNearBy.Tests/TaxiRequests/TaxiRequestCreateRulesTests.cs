using System.Text.Json;
using System.Text.Json.Nodes;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Core.Entities;
using RentNearBy.Core.Models;
using RentNearBy.Core.Models.TaxiRequests;

namespace RentNearBy.Tests.TaxiRequests;

// Contract T2 (one case per rule), P5 occupied range, P18 dispatch keys, P25 legacy block, P6 stale timer, T5 cancel.
public class TaxiRequestCreateRulesTests
{
    private const long First = 632970, Last = 1418230, Kathgodam = 6377270, Haldwani = 2552100, Ramnagar = 2738820;
    private static readonly DateOnly Today = new(2026, 12, 10);
    private static readonly DateOnly Start = new(2026, 12, 18);

    private static TaxiCreateFacts Facts(DateOnly? start = null, int dayCount = 4, bool completed = false, DateOnly? today = null,
        long[]? pickup = null, long[]? drop = null, int sightseeingDrivers = 15, bool noStart = false, long? sightseeingCity = null, int travellers = 2)
        => new(noStart ? null : start ?? Start, dayCount, completed, today ?? Today, First, Last,
            pickup ?? [Kathgodam, Haldwani], drop ?? [Ramnagar], sightseeingDrivers, sightseeingCity, travellers);

    private static CreateTaxiRequestDto Body(bool pickup = true, long? pickupKey = Kathgodam, bool sightseeing = true, int[]? days = null,
        bool drop = true, bool same = true, long? dropKey = null, int seats = 4)
        => new()
        {
            ClientRequestId = Guid.NewGuid(),
            Pickup = new CreateTaxiPickupDto { On = pickup, CityKey = pickupKey },
            Sightseeing = new CreateTaxiSightseeingDto { On = sightseeing, Days = (days ?? [1, 2, 4]).ToList() },
            Drop = new CreateTaxiDropDto { On = drop, SameAsPickup = same, CityKey = dropKey },
            Seats = seats,
        };

    private static void Refused(TaxiCreateCheck check, int status, string type)
    {
        Assert.Null(check.Plan);
        Assert.NotNull(check.Error);
        Assert.Equal(status, check.Error!.Status);
        Assert.Equal(type, check.Error.Type);
    }

    [Fact]
    public void Contract_sample_body_passes_every_rule()
    {
        var text = File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "taxi_request_create.json"));
        var body = JsonSerializer.Deserialize<CreateTaxiRequestDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var check = TaxiRequestCreateRules.Check(body, Facts());
        Assert.Null(check.Error);
        Assert.Equal(new TaxiDateRange(Start, Start.AddDays(3)), check.Plan!.Occupied);
        Assert.Equal(Kathgodam, check.Plan.DropCityKey);
        Assert.True(check.Plan.DropSameAsPickup);
        Assert.Equal([1, 2, 4], check.Plan.SightseeingDays);
    }

    [Fact]
    public void All_sections_off_is_taxi_sections_required()
        => Refused(TaxiRequestCreateRules.Check(Body(pickup: false, sightseeing: false, drop: false), Facts()), 400, TaxiRequestErrorTypes.TaxiSectionsRequired);

    [Fact]
    public void Pickup_city_not_in_the_list_is_pickup_not_available()
        => Refused(TaxiRequestCreateRules.Check(Body(pickupKey: Ramnagar), Facts()), 409, TaxiRequestErrorTypes.PickupNotAvailable);

    [Fact]
    public void Pickup_without_a_city_is_pickup_not_available()
        => Refused(TaxiRequestCreateRules.Check(Body(pickupKey: null), Facts()), 409, TaxiRequestErrorTypes.PickupNotAvailable);

    [Fact]
    public void Drop_same_as_pickup_needs_pickup_on()
        => Refused(TaxiRequestCreateRules.Check(Body(pickup: false, same: true), Facts()), 409, TaxiRequestErrorTypes.DropNotAvailable);

    [Fact]
    public void Drop_city_not_in_the_list_is_drop_not_available()
        => Refused(TaxiRequestCreateRules.Check(Body(same: false, dropKey: Haldwani), Facts()), 409, TaxiRequestErrorTypes.DropNotAvailable);

    [Fact]
    public void Drop_city_from_the_drop_list_is_accepted()
    {
        var check = TaxiRequestCreateRules.Check(Body(same: false, dropKey: Ramnagar), Facts());
        Assert.Equal(Ramnagar, check.Plan!.DropCityKey);
        Assert.False(check.Plan.DropSameAsPickup);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 5 })]
    public void Empty_or_unknown_sightseeing_days_are_invalid(int[] days)
        => Refused(TaxiRequestCreateRules.Check(Body(days: days), Facts()), 400, TaxiRequestErrorTypes.SightseeingDaysInvalid);

    [Fact]
    public void Sightseeing_day_in_the_past_is_invalid()
        => Refused(TaxiRequestCreateRules.Check(Body(pickup: false, drop: false, days: [1, 3]), Facts(today: Start.AddDays(1))),
            400, TaxiRequestErrorTypes.SightseeingDaysInvalid);

    [Fact]
    public void Sightseeing_only_without_any_driver_route_is_no_drivers_near_here()
        => Refused(TaxiRequestCreateRules.Check(Body(pickup: false, drop: false), Facts(sightseeingDrivers: 0)), 409, TaxiRequestErrorTypes.NoDriversNearHere);

    [Fact]
    public void Trip_without_dates_is_trip_dates_required()
        => Refused(TaxiRequestCreateRules.Check(Body(), Facts(noStart: true)), 409, TaxiRequestErrorTypes.TripDatesRequired);

    [Fact]
    public void Trip_without_days_is_trip_dates_required()
        => Refused(TaxiRequestCreateRules.Check(Body(), Facts(dayCount: 0)), 409, TaxiRequestErrorTypes.TripDatesRequired);

    [Fact]
    public void Completed_trip_is_refused()
        => Refused(TaxiRequestCreateRules.Check(Body(), Facts(completed: true)), 409, TripEnquiryErrorTypes.TripCompleted);

    [Fact]
    public void Pickup_on_a_trip_whose_first_day_passed_is_day_in_past()
        => Refused(TaxiRequestCreateRules.Check(Body(sightseeing: false, drop: false), Facts(today: Start.AddDays(1))), 409, TaxiRequestErrorTypes.DayInPast);

    [Fact]
    public void Drop_on_a_trip_whose_last_day_passed_is_day_in_past()
        => Refused(TaxiRequestCreateRules.Check(Body(pickup: false, sightseeing: false, same: false, dropKey: Ramnagar), Facts(today: Start.AddDays(4))),
            409, TaxiRequestErrorTypes.DayInPast);

    [Fact]
    public void Pickup_on_the_first_day_itself_is_allowed()
        => Assert.Null(TaxiRequestCreateRules.Check(Body(sightseeing: false, drop: false), Facts(today: Start)).Error);

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(13)]
    public void Seats_other_than_4_6_7_12_are_refused(int seats)
        => Refused(TaxiRequestCreateRules.Check(Body(seats: seats), Facts()), 400, TaxiRequestCreateRules.BadRequestType);

    // O2
    [Theory]
    [InlineData(4, 4)]
    [InlineData(6, 5)]
    [InlineData(7, 7)]
    [InlineData(12, 8)]
    [InlineData(12, 12)]
    public void Seats_that_hold_every_traveller_pass(int seats, int travellers)
    {
        var check = TaxiRequestCreateRules.Check(Body(seats: seats), Facts(travellers: travellers));
        Assert.Null(check.Error);
        Assert.Equal(seats, check.Plan!.Seats);
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(6, 7)]
    [InlineData(7, 8)]
    public void Seats_fewer_than_the_travellers_are_seats_too_few(int seats, int travellers)
        => Refused(TaxiRequestCreateRules.Check(Body(seats: seats), Facts(travellers: travellers)), 400, TaxiRequestErrorTypes.SeatsTooFew);

    [Theory]
    [InlineData(13)]
    [InlineData(30)]
    public void More_than_12_travellers_are_too_many_travellers(int travellers)
    {
        var check = TaxiRequestCreateRules.Check(Body(seats: 12), Facts(travellers: travellers));
        Refused(check, 400, TaxiRequestErrorTypes.TooManyTravellers);
        Assert.Equal("For groups above 12, please send separate requests.", check.Error!.Message);
    }

    // O4
    [Fact]
    public void Sightseeing_only_dispatches_to_the_sightseeing_city()
    {
        var plan = TaxiRequestCreateRules.Check(Body(pickup: false, drop: false, days: [3, 4]), Facts(sightseeingCity: Last)).Plan!;
        Assert.Equal((Last, Last, (long?)null), (plan.DispatchCityKey, plan.MatchToCityKey, plan.MatchFromCityKey));
    }

    [Fact]
    public void Sightseeing_city_does_not_move_a_pickup_or_drop_dispatch()
    {
        var pickup = TaxiRequestCreateRules.Check(Body(), Facts(sightseeingCity: Last)).Plan!;
        Assert.Equal((Kathgodam, First), (pickup.DispatchCityKey, pickup.MatchToCityKey));
        var drop = TaxiRequestCreateRules.Check(Body(pickup: false, same: false, dropKey: Ramnagar), Facts(sightseeingCity: First)).Plan!;
        Assert.Equal((Ramnagar, Last), (drop.DispatchCityKey, drop.MatchToCityKey));
    }

    [Fact]
    public void No_drivers_near_here_uses_the_sightseeing_city_pool()
        => Refused(TaxiRequestCreateRules.Check(Body(pickup: false, drop: false, days: [3]), Facts(sightseeingCity: Last, sightseeingDrivers: 0)),
            409, TaxiRequestErrorTypes.NoDriversNearHere);

    private static TripPlan TwoCityPlan() => new()
    {
        CityKey = First,
        CityName = "Nainital",
        Legs =
        [
            new TripCityLeg { Index = 1, CityKey = First, CityName = "Nainital" },
            new TripCityLeg { Index = 2, CityKey = Last, CityName = "Almora" },
        ],
        Days =
        [
            new TripDay { Day = 1, Leg = 1 },
            new TripDay { Day = 2, Leg = 1 },
            new TripDay { Day = 3, Leg = 2 },
            new TripDay { Day = 4, Leg = 2 },
        ],
    };

    [Theory]
    [InlineData(new[] { 3, 4 }, Last)]
    [InlineData(new[] { 4, 3 }, Last)]
    [InlineData(new[] { 1, 3 }, First)]
    [InlineData(new[] { 2 }, First)]
    [InlineData(new int[0], First)]
    public void Sightseeing_city_is_the_city_of_the_first_chosen_day(int[] days, long expected)
        => Assert.Equal(expected, TaxiRequestCreateRules.SightseeingCityOf(TwoCityPlan(), days, First));

    [Fact]
    public void Sightseeing_city_of_a_plan_without_legs_is_the_first_city()
        => Assert.Equal(First, TaxiRequestCreateRules.SightseeingCityOf(new TripPlan { CityKey = First }, [2], First));

    [Fact]
    public void Sightseeing_city_days_group_the_plan_days_by_city()
    {
        var cities = TaxiRequestCreateRules.SightseeingCityDays(TwoCityPlan(), First);
        Assert.Equal(2, cities.Count);
        Assert.Equal((First, "Nainital"), (cities[0].CityKey, cities[0].Name));
        Assert.Equal([1, 2], cities[0].Days);
        Assert.Equal((Last, "Almora"), (cities[1].CityKey, cities[1].Name));
        Assert.Equal([3, 4], cities[1].Days);
    }

    // O3 + O10
    [Fact]
    public void Overlap_error_names_the_other_trip()
    {
        var tripId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var error = TaxiRequestCreateRules.OverlapError(new TaxiOverlap(tripId, " Hills trip ", requestId, Start, Start.AddDays(2)));
        Assert.Equal((409, TaxiRequestErrorTypes.OverlappingTaxiRequest), (error.Status, error.Type));
        Assert.Equal("You already have a taxi request for these dates in Hills trip", error.Message);
        var data = Assert.IsType<OverlappingTaxiRequestData>(error.Data);
        Assert.Equal((tripId, "Hills trip", requestId, Start, Start.AddDays(2)), (data.TripId, data.TripName, data.RequestId, data.DateFrom, data.DateTo));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Overlap_error_without_a_trip_name_says_another_trip(string name)
    {
        var error = TaxiRequestCreateRules.OverlapError(new TaxiOverlap(Guid.NewGuid(), name, Guid.NewGuid(), Start, Start));
        Assert.Equal("You already have a taxi request for these dates in another trip", error.Message);
        Assert.Equal("another trip", ((OverlappingTaxiRequestData)error.Data!).TripName);
    }

    [Fact]
    public void Duplicate_sightseeing_days_are_stored_once_in_order()
        => Assert.Equal([1, 3], TaxiRequestCreateRules.Check(Body(days: [3, 1, 3]), Facts()).Plan!.SightseeingDays);

    [Fact]
    public void Occupied_range_by_sections()
    {
        TaxiDateRange? Range(CreateTaxiRequestDto b) => TaxiRequestCreateRules.Check(b, Facts(dayCount: 6)).Plan?.Occupied;
        DateOnly D(int day) => Start.AddDays(day - 1);

        Assert.Equal(new TaxiDateRange(D(1), D(1)), Range(Body(sightseeing: false, drop: false)));
        Assert.Equal(new TaxiDateRange(D(6), D(6)), Range(Body(pickup: false, sightseeing: false, same: false, dropKey: Ramnagar)));
        Assert.Equal(new TaxiDateRange(D(2), D(4)), Range(Body(pickup: false, drop: false, days: [4, 2])));
        Assert.Equal(new TaxiDateRange(D(1), D(3)), Range(Body(drop: false, days: [2, 3])));
        Assert.Equal(new TaxiDateRange(D(1), D(6)), Range(Body(days: [2])));
    }

    [Fact]
    public void Dispatch_keys_for_pickup_drop_only_and_sightseeing_only()
    {
        var pickup = TaxiRequestCreateRules.Check(Body(), Facts()).Plan!;
        Assert.Equal((Kathgodam, First, (long?)Kathgodam), (pickup.DispatchCityKey, pickup.MatchToCityKey, pickup.MatchFromCityKey));

        var dropOnly = TaxiRequestCreateRules.Check(Body(pickup: false, sightseeing: false, same: false, dropKey: Ramnagar), Facts()).Plan!;
        Assert.Equal((Ramnagar, Last, (long?)Ramnagar), (dropOnly.DispatchCityKey, dropOnly.MatchToCityKey, dropOnly.MatchFromCityKey));

        var sightseeingOnly = TaxiRequestCreateRules.Check(Body(pickup: false, drop: false), Facts()).Plan!;
        Assert.Equal((First, First, (long?)null), (sightseeingOnly.DispatchCityKey, sightseeingOnly.MatchToCityKey, sightseeingOnly.MatchFromCityKey));

        var sightseeingAndDrop = TaxiRequestCreateRules.Check(Body(pickup: false, same: false, dropKey: Ramnagar), Facts()).Plan!;
        Assert.Equal((Ramnagar, Last, (long?)Ramnagar), (sightseeingAndDrop.DispatchCityKey, sightseeingAndDrop.MatchToCityKey, sightseeingAndDrop.MatchFromCityKey));
    }

    private static TripEnquiry Row(string status, bool journeyCar = false, string kind = TripEnquiryKinds.Taxi) => new()
    {
        Id = Guid.NewGuid(),
        TripId = Guid.NewGuid(),
        Kind = kind,
        Status = status,
        Day = journeyCar ? (short)1 : null,
        ContextJson = journeyCar ? "{\"journeyId\":\"" + Guid.NewGuid() + "\",\"legIndex\":1}" : "{}",
    };

    [Fact]
    public void Legacy_local_taxi_ask_blocks_create_and_lists_the_rows()
    {
        var asked = Row(TripEnquiryStatuses.Asked);
        var fare = Row(TripEnquiryStatuses.FareSent);
        var confirmed = Row(TripEnquiryStatuses.ConfirmedByBoth);
        var booked = Row(TripEnquiryStatuses.BookedByTraveller);
        var closed = Row(TripEnquiryStatuses.CancelledByTraveller);
        var stay = Row(TripEnquiryStatuses.Asked, kind: TripEnquiryKinds.Stay);

        var block = TaxiRequestCreateRules.LegacyBlock([asked, fare, confirmed, booked, closed, stay]);

        Assert.NotNull(block);
        Assert.Equal([asked.Id, fare.Id, confirmed.Id, booked.Id], block!.Enquiries.Select(e => e.Id));
        Assert.Equal([false, false, true, true], block.Enquiries.Select(e => e.Booked));
        Assert.Null(TaxiRequestCreateRules.LegacyBlock([closed, stay]));
    }

    [Fact]
    public void Journey_car_rows_do_not_block_create()
    {
        var car = Row(TripEnquiryStatuses.ConfirmedByBoth, journeyCar: true);
        var carAsk = Row(TripEnquiryStatuses.Asked, journeyCar: true);
        Assert.True(TripSegmentRows.IsSegment(car));
        Assert.Null(TaxiRequestCreateRules.LegacyBlock([car, carAsk]));
    }

    [Fact]
    public void Legacy_error_equals_the_contract_sample()
    {
        var row = Row(TripEnquiryStatuses.Asked);
        row.Id = Guid.Parse("c7d1e2f3-4a5b-4c6d-8e9f-0a1b2c3d4e5f");
        var error = TaxiRequestCreateRules.LegacyError(TaxiRequestCreateRules.LegacyBlock([row])!);
        var envelope = new ApiResponse<LegacyTaxiActiveData>
        {
            Data = (LegacyTaxiActiveData)error.Data!,
            Status = error.Status.ToString(),
            Code = error.Status,
            Timestamp = 1797500000,
            Error = new ApiError { Message = error.Message, Type = error.Type },
        };

        var written = JsonNode.Parse(JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractSamplesGuardTests.SamplesDir, "error_legacy_taxi_active.json")));
        Assert.True(JsonNode.DeepEquals(sample, written), written!.ToJsonString());
    }

    [Fact]
    public void Stale_timer_is_due_at_midnight_ist_after_the_first_needed_day()
    {
        var due = TaxiDispatchRules.StaleDueAt(Start);
        Assert.Equal(new DateTime(2026, 12, 18, 18, 30, 0, DateTimeKind.Utc), due);
        Assert.Equal(DateTimeKind.Utc, due.Kind);
        Assert.Equal(Start.AddDays(1), IstTime.TodayIst(due));
        Assert.Equal(Start, IstTime.TodayIst(due.AddTicks(-1)));
    }

    [Fact]
    public void Same_day_request_is_not_due_at_once()
    {
        var lateEvening = new DateTime(2026, 12, 18, 18, 0, 0, DateTimeKind.Utc);
        Assert.Equal(Start, IstTime.TodayIst(lateEvening));
        var plan = TaxiRequestCreateRules.Check(Body(sightseeing: false, drop: false), Facts(today: IstTime.TodayIst(lateEvening))).Plan!;
        Assert.True(TaxiDispatchRules.StaleDueAt(plan.Occupied.From) > lateEvening);
    }

    [Fact]
    public void Far_request_has_no_earlier_time_limit_than_its_first_needed_day()
    {
        var plan = TaxiRequestCreateRules.Check(Body(pickup: false, drop: false, days: [4]), Facts()).Plan!;
        Assert.Equal(IstTime.IstMidnightUtc(Start.AddDays(4)), TaxiDispatchRules.StaleDueAt(plan.Occupied.From));
    }

    [Theory]
    [InlineData(null, null, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(2, null, 2)]
    [InlineData(3, 2, 5)]
    public void Travellers_come_from_the_plan_people_with_at_least_one(int? adults, int? kids, int expected)
        => Assert.Equal(expected, TaxiRequestCreateRules.TravellersOf(new TripSettings { Adults = adults, Kids = kids }));

    [Fact]
    public void Plan_without_settings_has_one_traveller()
        => Assert.Equal(1, TaxiRequestCreateRules.TravellersOf(null));

    [Theory]
    [InlineData(RequestStates.Open)]
    [InlineData(RequestStates.Collecting)]
    [InlineData(RequestStates.Selected)]
    public void Cancel_before_booking_needs_no_chip(string state)
    {
        var check = TaxiRequestCreateRules.CancelCheck(state, RequestEndReasons.PickupChanged, null);
        Assert.Equal(RequestEndReasons.PickupChanged, check.Plan!.EndReason);
        Assert.Null(check.Plan.Chip);
    }

    [Fact]
    public void Booked_cancel_needs_a_chip_and_ends_as_booking_cancelled_by_traveller()
    {
        Assert.Equal(TaxiRequestErrorTypes.ReasonRequired, TaxiRequestCreateRules.CancelCheck(RequestStates.Booked, RequestEndReasons.TravellerCancelled, null).Error!.Type);
        var check = TaxiRequestCreateRules.CancelCheck(RequestStates.Booked, RequestEndReasons.TravellerCancelled, ReasonChips.ChangedPlan);
        Assert.Equal(RequestEndReasons.BookingCancelledByTraveller, check.Plan!.EndReason);
        Assert.Equal(ReasonChips.ChangedPlan, check.Plan.Chip);
        Assert.Equal(BookingCancelledBy.Traveller, check.Plan.CancelledBy);
    }

    [Theory]
    [InlineData(RequestStates.InProgress)]
    [InlineData(RequestStates.Completed)]
    [InlineData(RequestStates.Expired)]
    public void Cancel_in_progress_or_ended_is_state_invalid(string state)
        => Assert.Equal(TaxiRequestErrorTypes.TaxiRequestStateInvalid, TaxiRequestCreateRules.CancelCheck(state, RequestEndReasons.TravellerCancelled, null).Error!.Type);

    [Fact]
    public void Cancel_of_a_cancelled_request_is_a_repeat()
        => Assert.True(TaxiRequestCreateRules.CancelCheck(RequestStates.Cancelled, RequestEndReasons.TravellerCancelled, null).AlreadyDone);

    [Theory]
    [InlineData("AdminCancelled", null)]
    [InlineData(null, null)]
    [InlineData("TravellerCancelled", "Rude")]
    public void Cancel_reason_and_chip_must_be_contract_values(string? reason, string? chip)
        => Assert.Equal(400, TaxiRequestCreateRules.CancelCheck(RequestStates.Open, reason, chip).Error!.Status);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Admin_note_must_not_be_blank(string? note)
    {
        var (text, error) = TaxiRequestCreateRules.AdminNoteCheck(note);
        Assert.Null(text);
        Assert.Equal((400, TaxiRequestCreateRules.BadRequestType), (error!.Status, error.Type));
    }

    [Fact]
    public void Admin_note_over_300_characters_is_refused()
        => Assert.Equal(400, TaxiRequestCreateRules.AdminNoteCheck(new string('a', 301)).Error!.Status);

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Admin_note_of_1_to_300_characters_is_kept_trimmed(int length)
    {
        var body = new string('a', length);
        var (text, error) = TaxiRequestCreateRules.AdminNoteCheck("  " + body + "  ");
        Assert.Null(error);
        Assert.Equal(body, text);
    }
}
