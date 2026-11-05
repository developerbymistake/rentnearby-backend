using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Trips;

// CONTRACT_MCD D1: journey cars only under legs[].travel.cars, nights and local taxis grouped per city.
public class TripSummaryRecordBuilderTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateTime Now = new(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TripId = Guid.Parse("cccccccc-0000-0000-0000-0000000000e1");
    private static readonly Guid Sunil = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Mohan = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static Trip NewTrip(int days) => new()
    {
        Id = TripId,
        Title = "Haridwar, Badrinath trip",
        CityName = "Haridwar",
        Status = TripStatuses.Planned,
        StartDate = Start,
        EndDate = Start.AddDays(days - 1),
    };

    private static TripPlan Plan(params int[] legOfDay)
    {
        var legCount = legOfDay.Max();
        var names = new[] { "Haridwar", "Badrinath", "Joshimath" };
        var plan = new TripPlan
        {
            CityKey = 210000001,
            CityName = names[0],
            Settings = new TripSettings { StartDate = Start, Adults = 4 },
            Legs = Enumerable.Range(1, legCount)
                .Select(i => new TripCityLeg { Index = i, CityKey = 210000000 + i, CityName = names[i - 1], Latitude = 30, Longitude = 78 })
                .ToList(),
        };
        for (var d = 1; d <= legOfDay.Length; d++)
            plan.Days.Add(new TripDay
            {
                Day = d,
                Leg = legOfDay[d - 1],
                Stay = new TripStayChoice { Name = legOfDay[d - 1] == 1 ? "Ganga View Inn" : "Alaknanda View", IsOwn = true },
            });
        return plan;
    }

    private static SavedJourney Journey()
    {
        var j = new SavedJourney { Id = Guid.NewGuid(), TripId = TripId, TravelDate = Start.AddDays(1), Cars = 2, People = 4, ListedTotal = 14000 };
        j.Legs.Add(new SavedJourneyLeg
        {
            Id = Guid.NewGuid(), JourneyId = j.Id, LegIndex = 1, FromName = "Haridwar", ToName = "Rishikesh",
            DriverTaxiId = Sunil, ListedFare = 4000, LastQuotedFare = 4200, LastEnquiryId = Guid.NewGuid(),
        });
        j.Legs.Add(new SavedJourneyLeg
        {
            Id = Guid.NewGuid(), JourneyId = j.Id, LegIndex = 2, FromName = "Rishikesh", ToName = "Badrinath",
            DriverTaxiId = Mohan, ListedFare = 9800, LastEnquiryId = Guid.NewGuid(),
        });
        return j;
    }

    private static Dictionary<Guid, JourneyTaxiInfo> Taxis() => new()
    {
        [Sunil] = new JourneyTaxiInfo(Sunil, Guid.NewGuid(), "Sunil Kumar", "Sedan", 4, null, "Haridwar", "new", "9876543210", null, true),
        [Mohan] = new JourneyTaxiInfo(Mohan, Guid.NewGuid(), "Mohan Bisht", "SUV", 6, null, "Joshimath", "new", "9876543211", null, true),
    };

    private static TripEnquiry SegmentRow(Guid journeyId, int car, int day, string status, int? fare) => new()
    {
        Id = Guid.NewGuid(),
        TripId = TripId,
        Kind = TripEnquiryKinds.Taxi,
        ListingId = car == 1 ? Sunil : Mohan,
        ListingName = "Taxi",
        Day = (short)day,
        DaysJson = $"[{day}]",
        CheckIn = Start.AddDays(day - 1),
        CheckOut = Start.AddDays(day),
        Status = status,
        AgreedFare = fare,
        PickupTime = "06:00",
        BookedByTravellerAt = Now,
        ConfirmedByTravellerAt = status == TripEnquiryStatuses.ConfirmedByBoth ? Now : null,
        ConfirmedByOwnerAt = status == TripEnquiryStatuses.ConfirmedByBoth ? Now : null,
        UpdatedAt = Now,
        ContextJson = $$"""{"city":"Badrinath","journeyId":"{{journeyId}}","legIndex":{{car}}}""",
    };

    private static TripEnquiry LocalTaxi(string daysJson, int firstDay, int lastDay, int fare) => new()
    {
        Id = Guid.NewGuid(),
        TripId = TripId,
        Kind = TripEnquiryKinds.Taxi,
        ListingId = Guid.NewGuid(),
        ListingName = "Ramesh Taxi",
        DaysJson = daysJson,
        CheckIn = Start.AddDays(firstDay - 1),
        CheckOut = Start.AddDays(lastDay),
        Status = TripEnquiryStatuses.BookedByTraveller,
        AgreedFare = fare,
        BookedByTravellerAt = Now,
        UpdatedAt = Now,
    };

    private static TripEnquiryDto Dto(TripEnquiry e, string ownerName = "")
    {
        var ctx = TripEnquiryContexts.Read(e.ContextJson);
        return new TripEnquiryDto
        {
            Id = e.Id,
            TripId = e.TripId,
            Kind = e.Kind,
            ListingId = e.ListingId,
            ListingName = e.ListingName,
            Day = e.Day,
            Days = string.IsNullOrEmpty(e.DaysJson) ? null : System.Text.Json.JsonSerializer.Deserialize<int[]>(e.DaysJson),
            CheckIn = e.CheckIn,
            CheckOut = e.CheckOut,
            Status = e.Status,
            DisplayStatus = e.Status == TripEnquiryStatuses.ConfirmedByBoth ? TripDisplayStatuses.ConfirmedByBoth : "bookedByYou",
            IsSegment = TripSegmentRows.IsSegment(e),
            Context = new TripEnquiryContextDto { JourneyId = ctx.JourneyId, JourneyLegIndex = ctx.LegIndex },
            Taxi = new TripTaxiDto { AgreedFare = e.AgreedFare, PickupTime = e.PickupTime },
            Owner = new TripOwnerPartyDto { Name = ownerName },
            UpdatedAt = e.UpdatedAt,
        };
    }

    private static TripSummaryRecordDto Build(Trip trip, TripPlan plan, IReadOnlyList<TripEnquiry> raw, IReadOnlyList<SavedJourney> journeys)
        => TripSummaryRecordBuilder.Build(trip, plan, raw.Select(r => Dto(r)).ToList(), raw, Now, journeys, Taxis());

    private static (TripPlan Plan, SavedJourney Journey, TripEnquiry Car1, TripEnquiry Local) TwoCities()
    {
        var plan = Plan(1, 2, 2, 2);
        var journey = Journey();
        plan.Legs![1].Arrival = new TripTravel
        {
            Mode = TripTravelModes.Journey, FromName = "Haridwar", JourneyId = journey.Id, Cars = 2, ListedTotal = 14000, RoadMinutes = 600,
        };
        return (plan, journey, SegmentRow(journey.Id, 1, 2, TripEnquiryStatuses.ConfirmedByBoth, 4200), LocalTaxi("[3,4]", 3, 4, 3000));
    }

    [Fact]
    public void Journey_car_is_travel_not_a_local_taxi()
    {
        var (plan, journey, car1, local) = TwoCities();
        var record = Build(NewTrip(4), plan, [car1, local], [journey]);

        Assert.DoesNotContain(record.Taxis, t => t.EnquiryId == car1.Id);
        Assert.Contains(record.Taxis, t => t.EnquiryId == local.Id);

        var travel = record.Legs[1].Travel!;
        Assert.Equal(TripTravelModes.Journey, travel.Mode);
        Assert.Equal("Haridwar", travel.FromName);
        Assert.Equal("Badrinath", travel.ToName);
        Assert.Equal(2, travel.Day);
        Assert.Equal(Start.AddDays(1), travel.Date);
        Assert.Equal(TripTravelStates.PartlyBooked, travel.State);
        Assert.Equal(2, travel.CarCount);
        Assert.Equal(14000, travel.ListedTotal);
        Assert.Equal(600, travel.RoadMinutes);
        Assert.Equal(2, travel.Cars.Count);

        var first = travel.Cars[0];
        Assert.Equal(1, first.Car);
        Assert.Equal("Sunil Kumar", first.DriverName);
        Assert.Equal("Sedan", first.VehicleTypeName);
        Assert.Equal("Rishikesh", first.ToName);
        Assert.Equal(4200, first.AgreedFare);
        Assert.Equal("both", first.ConfirmedBy);
        Assert.Equal(car1.Id, first.EnquiryId);
        Assert.Equal("06:00", first.PickupTime);
        Assert.Equal(TripDisplayStatuses.ConfirmedByBoth, first.DisplayStatus);

        var second = travel.Cars[1];
        Assert.Equal("Mohan Bisht", second.DriverName);
        Assert.Equal(9800, second.ListedFare);
        Assert.Null(second.EnquiryId);
        Assert.Equal(TripDisplayStatuses.Asked, second.DisplayStatus);
    }

    [Fact]
    public void Local_taxi_and_nights_are_grouped_by_city()
    {
        var (plan, journey, car1, local) = TwoCities();
        var record = Build(NewTrip(4), plan, [car1, local], [journey]);

        Assert.Equal(2, record.Legs.Count);
        Assert.Null(record.Legs[0].Travel);
        Assert.Equal(new[] { 1 }, record.Legs[0].Nights.Select(n => n.Night));
        Assert.Equal(new[] { 2, 3, 4 }, record.Legs[1].Nights.Select(n => n.Night));
        Assert.Equal(4, record.Nights.Count);
        Assert.Empty(record.Legs[0].Taxis);
        Assert.Single(record.Legs[1].Taxis, t => t.EnquiryId == local.Id);
        Assert.Equal(1, record.Legs[0].StartDay);
        Assert.Equal(Start.AddDays(1), record.Legs[1].StartDate);
        Assert.Equal(Start.AddDays(3), record.Legs[1].EndDate);
    }

    [Fact]
    public void Agreed_total_includes_the_booked_journey_car()
    {
        var (plan, journey, car1, local) = TwoCities();
        var record = Build(NewTrip(4), plan, [car1, local], [journey]);
        Assert.Equal(7200, record.AgreedTotal);
    }

    [Fact]
    public void One_city_record_has_one_leg_and_no_travel()
    {
        var plan = Plan(1, 1, 1);
        var local = LocalTaxi("[1,2]", 1, 2, 2500);
        var record = Build(NewTrip(3), plan, [local], []);

        var leg = Assert.Single(record.Legs);
        Assert.Null(leg.Travel);
        Assert.Equal("Haridwar", leg.CityName);
        Assert.Equal(new[] { 1, 2, 3 }, record.Nights.Select(n => n.Night));
        Assert.Equal(record.Nights, leg.Nights);
        Assert.Single(record.Taxis);
        Assert.Equal(record.Taxis, leg.Taxis);
        Assert.Equal(2500, record.AgreedTotal);
    }

    [Fact]
    public void Own_travel_card_has_no_cars()
    {
        var plan = Plan(1, 2, 2);
        plan.Legs![1].Arrival = new TripTravel
        {
            Mode = TripTravelModes.Own, FromName = "Haridwar", How = "bus", OwnName = "Shatabdi", OwnPhone = "9876543210", LeavesAt = "06:00",
        };
        var travel = Build(NewTrip(3), plan, [], []).Legs[1].Travel!;

        Assert.Equal(TripTravelModes.Own, travel.Mode);
        Assert.Equal(TripTravelStates.Own, travel.State);
        Assert.Equal(0, travel.CarCount);
        Assert.Empty(travel.Cars);
        Assert.Equal("bus", travel.How);
        Assert.Equal("Shatabdi", travel.OwnName);
        Assert.Equal("9876543210", travel.OwnPhone);
        Assert.Equal("06:00", travel.LeavesAt);
        Assert.Null(travel.JourneyId);
    }

    [Fact]
    public void No_travel_chosen_gives_no_card()
        => Assert.Null(Build(NewTrip(3), Plan(1, 2, 2), [], []).Legs[1].Travel);

    [Fact]
    public void Missing_journey_builds_cars_from_the_rows()
    {
        var (plan, _, car1, local) = TwoCities();
        var raw = new[] { car1, local };
        var dtos = raw.Select(r => Dto(r, r.Id == car1.Id ? "Sunil K" : "")).ToList();
        var record = TripSummaryRecordBuilder.Build(NewTrip(4), plan, dtos, raw, Now, [], Taxis());

        var travel = record.Legs[1].Travel!;
        Assert.Equal(TripTravelStates.PartlyBooked, travel.State);
        var car = Assert.Single(travel.Cars);
        Assert.Equal(1, car.Car);
        Assert.Equal("Sunil K", car.DriverName);
        Assert.Equal(4200, car.AgreedFare);
        Assert.Equal(Sunil, car.TaxiId);
        Assert.Equal("Haridwar", car.FromName);
        Assert.Equal(string.Empty, car.ToName);
        Assert.DoesNotContain(record.Taxis, t => t.EnquiryId == car1.Id);
    }

    [Fact]
    public void Missing_journey_with_every_car_booked_reads_booked_with_the_route_ends()
    {
        var (plan, journey, car1, local) = TwoCities();
        var car2 = SegmentRow(journey.Id, 2, 2, TripEnquiryStatuses.BookedByTraveller, 9800);
        var travel = Build(NewTrip(4), plan, [car1, car2, local], []).Legs[1].Travel!;

        Assert.Equal(TripTravelStates.Booked, travel.State);
        Assert.Equal(2, travel.Cars.Count);
        Assert.Equal("Haridwar", travel.Cars[0].FromName);
        Assert.Equal("Badrinath", travel.Cars[1].ToName);
        Assert.Equal("Haridwar", travel.FromName);
        Assert.Equal("Badrinath", travel.ToName);
    }

    [Fact]
    public void Missing_journey_with_only_cancelled_cars_is_not_planned()
    {
        var (plan, journey, _, local) = TwoCities();
        var cancelled = SegmentRow(journey.Id, 1, 2, TripEnquiryStatuses.CancelledByTraveller, 4200);
        var travel = Build(NewTrip(4), plan, [cancelled, local], []).Legs[1].Travel!;

        Assert.Equal(TripTravelStates.NotPlanned, travel.State);
        Assert.Equal("Haridwar", Assert.Single(travel.Cars).FromName);
    }

    [Fact]
    public void Rows_state_reads_date_changed_for_a_booked_car_on_another_day()
    {
        var journeyId = Guid.NewGuid();
        var rows = new[] { SegmentRow(journeyId, 1, 3, TripEnquiryStatuses.ConfirmedByBoth, 4200) };
        Assert.Equal(TripTravelStates.DateChanged, TripTravelState.OfRows(rows, 1, Start.AddDays(1)));
        Assert.Equal(TripTravelStates.Confirmed, TripTravelState.OfRows(rows, 1, Start.AddDays(2)));
        Assert.Equal(TripTravelStates.NotPlanned, TripTravelState.OfRows([], 2, Start.AddDays(1)));
    }

    [Fact]
    public void Rows_of_a_replaced_journey_are_not_on_the_record()
    {
        var (plan, journey, _, local) = TwoCities();
        var stale = SegmentRow(Guid.NewGuid(), 1, 2, TripEnquiryStatuses.ConfirmedByBoth, 5000);
        var record = Build(NewTrip(4), plan, [stale, local], [journey]);

        Assert.DoesNotContain(record.Taxis, t => t.EnquiryId == stale.Id);
        Assert.All(record.Legs[1].Travel!.Cars, c => Assert.NotEqual(stale.Id, c.EnquiryId));
    }
}
