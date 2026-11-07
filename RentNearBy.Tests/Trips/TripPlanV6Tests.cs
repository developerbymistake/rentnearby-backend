using System.Text.Json;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;

namespace RentNearBy.Tests.Trips;

// PlanJson v6: the contract body round trip, row marks, steps without own fields, journey-only arrivals, dates and people optional.
public class TripPlanV6Tests
{
    private static readonly SaveTripRequestValidator Validator = new();

    private const string Body = """
    {
      "title": "Nainital + Almora",
      "status": "Draft",
      "clientTripId": "7f0e1c2a-1111-2222-3333-444455556666",
      "plan": {
        "version": 6,
        "cityKey": 210000123, "districtId": "56d120de-24b2-52de-9d38-d0c64689666d", "cityName": "Nainital",
        "centreLatitude": 29.38, "centreLongitude": 79.45,
        "legs": [
          { "index": 1, "cityKey": 210000123, "cityName": "Nainital", "districtId": "56d120de-24b2-52de-9d38-d0c64689666d", "latitude": 29.38, "longitude": 79.45,
            "stayMark": { "status": "own" } },
          { "index": 2, "cityKey": 210000456, "cityName": "Almora", "latitude": 29.59, "longitude": 79.65, "notReady": true,
            "arrival": { "fromName": "Nainital", "journeyId": "b2c3d4e5-2222-3333-4444-555555555555", "planKey": null, "cars": 2, "listedTotal": 5600, "roadMinutes": 100, "ownCars": [2] },
            "stayMark": null, "travelMark": null }
        ],
        "days": [
          { "day": 1, "leg": 1, "spots": [ { "id": "Q123", "name": "Naini Lake", "latitude": 29.39, "longitude": 79.46, "category": "lake", "visitMinutes": 90, "imageUrl": null, "closedDays": null } ] },
          { "day": 2, "leg": 1, "restDay": true, "spots": [] },
          { "day": 3, "leg": 2, "spots": [] }
        ],
        "settings": { "dayLimitMinutes": 300, "startDate": "2026-11-20", "adults": 2, "kids": 0, "maxDays": null },
        "steps": {
          "startFrom": { "name": "Delhi", "lat": 28.61, "lon": 77.21 },
          "goingHomeTo": null,
          "gettingThere": { "status": "chosen", "journeyId": "a1b2c3d4-2222-3333-4444-555555555555", "date": null, "ownCars": [] },
          "comingBack": { "status": "skipped", "leaveOn": "checkOut" }
        },
        "taxiMark": { "status": "skipped" },
        "dismissedEnquiryIds": []
      }
    }
    """;

    private static SaveTripRequest Parse(string json) => JsonSerializer.Deserialize<SaveTripRequest>(json, TripJson.Options)!;

    private static TripCityLeg Leg(int index, long key, string name) => new() { Index = index, CityKey = key, CityName = name, Latitude = 29.38, Longitude = 79.45 };

    private static TripPlan Plan() => new()
    {
        CityKey = 210000123,
        Legs = [Leg(1, 210000123, "Nainital")],
        Days = [new TripDay { Day = 1, Spots = [new TripSpot { Id = "a", Name = "Lake", Latitude = 29.39, Longitude = 79.45, VisitMinutes = 60 }] }],
    };

    [Fact]
    public void Version_IsSix_AndOnlySixIsSupported()
    {
        Assert.Equal(6, TripPlan.CurrentVersion);
        Assert.Equal(new[] { 6 }, TripPlan.SupportedVersions);
        Assert.Equal(6, new TripPlan().Version);
    }

    [Fact]
    public void ContractBody_ParsesAndValidates()
    {
        var request = Parse(Body);

        ValidationAssert.Valid(Validator.Validate(request));
        var plan = request.Plan;
        Assert.Equal(TripRowMarkStatuses.Own, plan.Legs![0].StayMark!.Status);
        Assert.Null(plan.Legs[1].StayMark);
        Assert.Equal(TripRowMarkStatuses.Skipped, plan.TaxiMark!.Status);
        Assert.Equal(Guid.Parse("b2c3d4e5-2222-3333-4444-555555555555"), plan.Legs[1].Arrival!.JourneyId);
        Assert.Equal(TripStepStatuses.Skipped, plan.Steps!.ComingBack!.Status);
        Assert.True(plan.Days[1].RestDay);
        Assert.Equal(300, plan.Settings.DayLimitMinutes);
    }

    [Fact]
    public void ContractBody_RoundTripsThroughTheSerializer()
    {
        var plan = Parse(Body).Plan;

        var back = JsonSerializer.Deserialize<TripPlan>(JsonSerializer.Serialize(plan, TripJson.Options), TripJson.Options)!;

        Assert.Equal(6, back.Version);
        Assert.Equal(2, back.Legs!.Count);
        Assert.Equal(TripRowMarkStatuses.Own, back.Legs[0].StayMark!.Status);
        Assert.Equal(TripRowMarkStatuses.Skipped, back.TaxiMark!.Status);
        Assert.Equal(new[] { 2 }, back.Legs[1].Arrival!.OwnCars);
        Assert.Equal(new DateOnly(2026, 11, 20), back.Settings.StartDate);
        Assert.Equal(TripLeaveOn.CheckOut, back.Steps!.ComingBack!.LeaveOn);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void OtherVersions_AreRefused(int version)
    {
        var request = Parse(Body);
        request.Plan.Version = version;

        ValidationAssert.HasMessage(Validator.Validate(request), "Unsupported plan version");
    }

    [Fact]
    public void DatesAndPeopleNull_SaveIsAccepted()
    {
        var request = Parse(Body);
        request.Plan.Settings.StartDate = null;
        request.Plan.Settings.Adults = null;
        request.Plan.Settings.Kids = null;

        ValidationAssert.Valid(Validator.Validate(request));
        Assert.Null(request.Plan.EndDate);
        Assert.Null(request.Plan.CheckOutDate);
        Assert.Null(request.Plan.ComingBackDate);
    }

    [Fact]
    public void SettingsObjectWithoutDatesOrPeople_ReadsDefaults()
    {
        var json = Body.Replace("\"startDate\": \"2026-11-20\", \"adults\": 2, \"kids\": 0, ", string.Empty);

        var request = Parse(json);

        ValidationAssert.Valid(Validator.Validate(request));
        Assert.Null(request.Plan.Settings.StartDate);
        Assert.Null(request.Plan.Settings.Adults);
        Assert.Null(request.Plan.Settings.Kids);
    }

    [Fact]
    public void Normalize_IsIdempotent_AndMirrorsTheFirstLeg()
    {
        var plan = Parse(Body).Plan;
        plan.CityName = "Stale";
        plan.Normalize();
        var once = JsonSerializer.Serialize(plan, TripJson.Options);

        plan.Normalize();

        Assert.Equal(once, JsonSerializer.Serialize(plan, TripJson.Options));
        Assert.Equal("Nainital", plan.CityName);
        Assert.Equal(210000123L, plan.CityKey);
        Assert.Equal(new[] { 1, 1, 2 }, plan.Days.Select(d => d.Leg!.Value));
        Assert.NotNull(plan.Steps);
    }

    [Fact]
    public void Normalize_GivesEveryPlanSteps()
    {
        var plan = Plan();
        Assert.Null(plan.Steps);

        plan.Normalize();

        Assert.NotNull(plan.Steps);
    }

    [Fact]
    public void Normalize_DoesNotTouchMarks()
    {
        var plan = Plan();
        plan.TaxiMark = new TripRowMark { Status = TripRowMarkStatuses.Own };
        plan.Legs![0].StayMark = new TripRowMark { Status = TripRowMarkStatuses.Skipped };

        plan.Normalize();

        Assert.Equal(TripRowMarkStatuses.Own, plan.TaxiMark.Status);
        Assert.Equal(TripRowMarkStatuses.Skipped, plan.Legs[0].StayMark!.Status);
    }

    [Fact]
    public void Normalize_KeepsLabelsShort()
    {
        var plan = Plan();
        plan.Steps = new TripSteps
        {
            StartFrom = new TripStartFrom { Name = " ", Lat = 28.61, Lon = 77.21 },
            GoingHomeTo = new TripStartFrom { Name = new string('a', 90), Lat = 26.9, Lon = 75.8 },
        };

        plan.Normalize();

        Assert.Equal("My location", plan.Steps.StartFrom!.Name);
        Assert.Equal(80, plan.Steps.GoingHomeTo!.Name.Length);
    }

    [Fact]
    public void Arrival_IsJourneyOnly()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, TripTravelRules.JourneyIdOf(new TripTravel { FromName = "Delhi", JourneyId = id }));
        Assert.Null(TripTravelRules.JourneyIdOf(new TripTravel { FromName = "Delhi" }));
        Assert.Null(TripTravelRules.JourneyIdOf(new TripTravel { FromName = "Delhi", JourneyId = Guid.Empty }));
        Assert.Null(TripTravelRules.JourneyIdOf(null));
    }

    [Fact]
    public void ArrivalIds_SkipLegOneAndMarksAreNotJourneys()
    {
        var id = Guid.NewGuid();
        var plan = new TripPlan
        {
            Legs =
            [
                Leg(1, 1, "A"),
                new TripCityLeg { Index = 2, CityKey = 2, CityName = "B", Arrival = new TripTravel { FromName = "A", JourneyId = id } },
                new TripCityLeg { Index = 3, CityKey = 3, CityName = "C", TravelMark = new TripRowMark { Status = TripRowMarkStatuses.Own } },
            ],
        };

        Assert.Equal(new List<Guid> { id }, TripTravelRules.ArrivalIds(plan));
    }

    [Fact]
    public void StepStatus_HasNoNotNeeded()
    {
        Assert.DoesNotContain("notNeeded", TripStepStatuses.All);
        Assert.Equal(TripStepStatuses.NotStarted, TripStepRules.StatusOf(new TripStep { Status = "notNeeded" }));
        Assert.Equal(TripStepStatuses.Own, TripStepRules.StatusOf(new TripStep { Status = TripStepStatuses.Own }));
    }

    [Fact]
    public void Serialize_HasNoV5Keys()
    {
        var plan = Parse(Body).Plan;
        plan.Days[0].RestDay = null;

        var json = JsonSerializer.Serialize(plan, TripJson.Options);

        foreach (var key in new[] { "\"taxi\"", "\"stay\"", "stayShortlist", "taxiShortlist", "\"rooms\"", "\"needs\"", "pickupTime", "pickupPlace", "ownName", "ownPhone", "leavesAt", "hubName", "\"mode\"", "\"how\"", "\"note\"" })
            Assert.DoesNotContain(key, json);
    }

    [Fact]
    public void CompatNeedsUpdate_OnlyForANewerDocument()
    {
        Assert.False(TripPlanCompat.NeedsUpdate(6));
        Assert.True(TripPlanCompat.NeedsUpdate(7));
    }

    [Fact]
    public void LeglessSavedRow_StillReadsAsOneLeg()
    {
        var trip = new Trip { CityKey = 210000123, CityName = "Haridwar", CityLatitude = 29.95, CityLongitude = 78.16, PlanJson = "{\"version\":6,\"days\":[]}" };

        var plan = TripPlanReader.Read(trip);

        var leg = Assert.Single(plan.Legs!);
        Assert.Equal("Haridwar", leg.CityName);
        Assert.NotNull(plan.Steps);
    }
}
