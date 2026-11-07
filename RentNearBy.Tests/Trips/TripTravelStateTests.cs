namespace RentNearBy.Tests.Trips;

// CONTRACT_MCB D7: first match wins. Row 1 (gap) is app session memory and has no server row.
public class TripTravelStateTests
{
    private static readonly DateOnly TravelDate = new(2026, 11, 3);
    private static readonly Guid TripId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static SavedJourney Journey(int cars = 2, DateOnly? date = null)
    {
        var j = new SavedJourney { Id = Guid.NewGuid(), TravelDate = date ?? TravelDate, Cars = (short)cars, People = 4 };
        for (var i = 1; i <= cars; i++)
            j.Legs.Add(new SavedJourneyLeg { Id = Guid.NewGuid(), JourneyId = j.Id, LegIndex = (short)i, FromName = $"From {i}", ToName = $"To {i}" });
        return j;
    }

    private static TripTravel Arrival(SavedJourney j) => new()
    {
        FromName = "Haridwar",
        JourneyId = j.Id,
        Cars = j.Legs.Count,
    };

    private static TripEnquiry Row(SavedJourney j, int car, string status) => new()
    {
        Id = Guid.NewGuid(),
        TripId = TripId,
        Kind = TripEnquiryKinds.Taxi,
        Day = 3,
        Status = status,
        CheckIn = TravelDate,
        ContextJson = $$"""{"city":"Badrinath","journeyId":"{{j.Id}}","legIndex":{{car}}}""",
    };

    private static SavedJourneyLeg Car(SavedJourney j, int i) => j.Legs.First(l => l.LegIndex == i);

    private static void Asked(SavedJourneyLeg car, string status = "Unknown")
    {
        car.LastEnquiryId = Guid.NewGuid();
        car.LastKnownStatus = status;
    }

    private static string Of(SavedJourney j, params TripEnquiry[] rows) => TripTravelState.Of(Arrival(j), j, rows, TravelDate);

    [Fact]
    public void States_are_the_wire_values()
    {
        Assert.Equal(11, TripTravelStates.All.Length);
        Assert.Equal(TripTravelStates.All.Length, TripTravelStates.All.Distinct().Count());
        Assert.Equal("notPlanned", TripTravelStates.NotPlanned);
        Assert.Equal("partlyBooked", TripTravelStates.PartlyBooked);
        Assert.Equal("dateChanged", TripTravelStates.DateChanged);
        Assert.Equal("carMissing", TripTravelStates.CarMissing);
        Assert.Equal("driverUnavailable", TripTravelStates.DriverUnavailable);
    }

    // Row 2
    [Fact]
    public void No_arrival_is_not_planned() => Assert.Equal(TripTravelStates.NotPlanned, TripTravelState.Of(null, null, [], TravelDate));

    // Row 4
    [Fact]
    public void Missing_deleted_or_other_journey_is_not_planned()
    {
        var j = Journey();
        Assert.Equal(TripTravelStates.NotPlanned, TripTravelState.Of(Arrival(j), null, [], TravelDate));
        j.IsDeleted = true;
        Assert.Equal(TripTravelStates.NotPlanned, Of(j));
        var other = Journey();
        Assert.Equal(TripTravelStates.NotPlanned, TripTravelState.Of(Arrival(other), Journey(), [], TravelDate));
    }

    // Row 5
    [Fact]
    public void Journey_on_another_date_is_date_changed()
        => Assert.Equal(TripTravelStates.DateChanged, Of(Journey(date: TravelDate.AddDays(1))));

    [Fact]
    public void Date_changed_beats_car_missing()
    {
        var j = Journey(date: TravelDate.AddDays(-1));
        Asked(Car(j, 2), JourneyLegStatuses.NotFree);
        Assert.Equal(TripTravelStates.DateChanged, Of(j));
    }

    [Fact]
    public void Without_a_travel_date_the_date_is_not_checked()
    {
        var j = Journey(date: TravelDate.AddDays(5));
        Assert.Equal(TripTravelStates.Saved, TripTravelState.Of(Arrival(j), j, [], null));
    }

    // Row 6
    [Fact]
    public void Driver_said_not_free_is_car_missing()
    {
        var j = Journey();
        Asked(Car(j, 1), JourneyLegStatuses.Free);
        Asked(Car(j, 2), JourneyLegStatuses.NotFree);
        Assert.Equal(TripTravelStates.CarMissing, Of(j));
    }

    [Fact]
    public void Every_live_row_of_an_asked_car_red_is_car_missing()
    {
        var j = Journey();
        Asked(Car(j, 2));
        Assert.Equal(TripTravelStates.CarMissing,
            Of(j, Row(j, 2, TripEnquiryStatuses.NotAvailable), Row(j, 2, TripEnquiryStatuses.Expired)));
    }

    [Fact]
    public void Car_missing_beats_driver_unavailable()
    {
        var j = Journey();
        Asked(Car(j, 1), JourneyLegStatuses.NotFree);
        Car(j, 2).DriverAvailable = false;
        Assert.Equal(TripTravelStates.CarMissing, Of(j));
    }

    // Row 7
    [Fact]
    public void Paused_driver_of_an_unbooked_car_is_driver_unavailable()
    {
        var j = Journey();
        Car(j, 1).DriverAvailable = false;
        Assert.Equal(TripTravelStates.DriverUnavailable, Of(j));
    }

    [Fact]
    public void Paused_driver_of_a_booked_car_stays_booked()
    {
        var j = Journey();
        Car(j, 1).DriverAvailable = false;
        Assert.Equal(TripTravelStates.Booked,
            Of(j, Row(j, 1, TripEnquiryStatuses.BookedByTraveller), Row(j, 2, TripEnquiryStatuses.BookedByTraveller)));
    }

    [Fact]
    public void Red_rows_of_a_booked_car_are_not_missing()
    {
        var j = Journey(cars: 1);
        Asked(Car(j, 1));
        Assert.Equal(TripTravelStates.Booked,
            Of(j, Row(j, 1, TripEnquiryStatuses.Declined), Row(j, 1, TripEnquiryStatuses.BookedByTraveller)));
    }

    // Row 8
    [Fact]
    public void Every_car_booked_is_booked_and_every_car_confirmed_is_confirmed()
    {
        var j = Journey();
        Assert.Equal(TripTravelStates.Booked,
            Of(j, Row(j, 1, TripEnquiryStatuses.ConfirmedByBoth), Row(j, 2, TripEnquiryStatuses.BookedByTraveller)));
        Assert.Equal(TripTravelStates.Confirmed,
            Of(j, Row(j, 1, TripEnquiryStatuses.ConfirmedByBoth), Row(j, 2, TripEnquiryStatuses.ConfirmedByBoth)));
    }

    // Row 9
    [Fact]
    public void Some_car_booked_is_partly_booked()
    {
        var j = Journey();
        Asked(Car(j, 2));
        Assert.Equal(TripTravelStates.PartlyBooked, Of(j, Row(j, 1, TripEnquiryStatuses.BookedByTraveller), Row(j, 2, TripEnquiryStatuses.Asked)));
    }

    [Fact]
    public void Closed_rows_never_count_as_booked()
    {
        var j = Journey(cars: 1);
        var closed = Row(j, 1, TripEnquiryStatuses.Closed);
        Assert.Equal(TripTravelStates.Saved, Of(j, closed));
    }

    // Row 10
    [Fact]
    public void Nothing_asked_is_saved() => Assert.Equal(TripTravelStates.Saved, Of(Journey()));

    // Row 11
    [Fact]
    public void Every_asked_car_free_is_free()
    {
        var j = Journey();
        Asked(Car(j, 1), JourneyLegStatuses.Free);
        Asked(Car(j, 2), JourneyLegStatuses.Free);
        Assert.Equal(TripTravelStates.Free, Of(j, Row(j, 1, TripEnquiryStatuses.FareSent), Row(j, 2, TripEnquiryStatuses.FareSent)));
    }

    // Row 12
    [Fact]
    public void Asked_without_every_answer_is_asked()
    {
        var j = Journey();
        Asked(Car(j, 1), JourneyLegStatuses.Free);
        Asked(Car(j, 2));
        Assert.Equal(TripTravelStates.Asked, Of(j, Row(j, 1, TripEnquiryStatuses.FareSent), Row(j, 2, TripEnquiryStatuses.Asked)));
    }
}
