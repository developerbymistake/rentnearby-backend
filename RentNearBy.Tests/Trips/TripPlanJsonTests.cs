using System.Text.Json;

namespace RentNearBy.Tests.Trips;

public class TripPlanJsonTests
{
    private static readonly Guid TaxiId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static TripPlan Sample() => new()
    {
        CityKey = 502141,
        CityName = "Nainital",
        CentreLatitude = 29.3919,
        CentreLongitude = 79.4542,
        Days =
        {
            new TripDay
            {
                Day = 1,
                Spots = { new TripSpot { Id = "osm:W1001", Name = "Mall Road", Latitude = 29.39, Longitude = 79.45, Category = "attraction", VisitMinutes = 90 } },
                Taxi = new TripTaxiChoice { TaxiId = TaxiId, Name = "Ramesh", Fare = 1800, VehicleTypeName = "Sedan" },
            },
            new TripDay
            {
                Day = 2,
                Spots = { new TripSpot { Id = "wiki:12345", Name = "Snow View", Latitude = 29.40, Longitude = 79.46, Category = "viewpoint", VisitMinutes = 120, ClosedDays = [1] } },
            },
        },
        Settings = new TripSettings { DayLimitMinutes = 450, StartDate = new DateOnly(2026, 11, 2) },
    };

    [Fact]
    public void RoundTrip_KeepsEveryField()
    {
        var json = JsonSerializer.Serialize(Sample(), TripJson.Options);

        var plan = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;

        Assert.Equal(TripPlan.CurrentVersion, plan.Version);
        Assert.Null(plan.DistrictId);
        Assert.Equal(502141L, plan.CityKey);
        Assert.Equal("Nainital", plan.CityName);
        Assert.Equal(2, plan.Days.Count);
        Assert.Equal("Mall Road", plan.Days[0].Spots[0].Name);
        Assert.Equal((Guid?)TaxiId, plan.Days[0].Taxi!.TaxiId);
        Assert.Equal((int?)1800, plan.Days[0].Taxi!.Fare);
        Assert.Equal("Sedan", plan.Days[0].Taxi!.VehicleTypeName);
        Assert.Equal(new[] { 1 }, plan.Days[1].Spots[0].ClosedDays);
        Assert.Equal(new DateOnly(2026, 11, 2), plan.Settings.StartDate);
    }

    [Fact]
    public void Serialize_CamelCaseNames()
    {
        var json = JsonSerializer.Serialize(Sample(), TripJson.Options);

        Assert.Contains("\"cityKey\":", json);
        Assert.Contains("\"centreLatitude\":", json);
        Assert.Contains("\"visitMinutes\":", json);
        Assert.Contains("\"dayLimitMinutes\":", json);
        Assert.Contains("\"startDate\":\"2026-11-02\"", json);
        Assert.Contains("\"vehicleTypeName\":\"Sedan\"", json);
        Assert.DoesNotContain("\"cityId\"", json);
        Assert.DoesNotContain("\"CityKey\"", json);
    }

    [Fact]
    public void Serialize_OmitsNulls()
    {
        var json = JsonSerializer.Serialize(Sample(), TripJson.Options);
        using var doc = JsonDocument.Parse(json);
        var day2 = doc.RootElement.GetProperty("days")[1];
        var spot1 = doc.RootElement.GetProperty("days")[0].GetProperty("spots")[0];

        Assert.False(doc.RootElement.TryGetProperty("districtId", out _));
        Assert.False(day2.TryGetProperty("taxi", out _));
        Assert.False(day2.TryGetProperty("stay", out _));
        Assert.False(spot1.TryGetProperty("imageUrl", out _));
        Assert.False(spot1.TryGetProperty("closedDays", out _));
    }

    [Fact]
    public void Deserialize_CaseInsensitive()
    {
        var json = $"{{\"Version\":2,\"CITYKEY\":502141,\"CITYNAME\":\"Nainital\",\"Days\":[{{\"Day\":1,\"Spots\":[{{\"Id\":\"osm:N1\",\"Name\":\"Lake\",\"VisitMinutes\":60}}]}}],\"Settings\":{{\"DayLimitMinutes\":400}}}}";

        var plan = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;

        Assert.Equal(502141L, plan.CityKey);
        Assert.Equal("Nainital", plan.CityName);
        Assert.Equal("Lake", plan.Days[0].Spots[0].Name);
        Assert.Equal(400, plan.Settings.DayLimitMinutes);
    }

    [Fact]
    public void Deserialize_V2WithoutNewFields_LeavesThemNull()
    {
        var json = "{\"version\":2,\"cityKey\":502141,\"cityName\":\"Nainital\",\"days\":[{\"day\":1,\"spots\":[],"
                   + "\"taxi\":{\"taxiId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"name\":\"Ramesh\",\"fare\":1800},"
                   + "\"stay\":{\"stayId\":\"bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee\",\"name\":\"Lake View\",\"pricePerNight\":2500}}],"
                   + "\"settings\":{\"dayLimitMinutes\":450,\"startDate\":\"2026-11-01\"}}";

        var plan = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;

        Assert.Equal(2, plan.Version);
        Assert.Null(plan.Settings.Adults);
        Assert.Null(plan.Settings.Kids);
        Assert.Null(plan.Settings.Rooms);
        Assert.Null(plan.Settings.Needs);
        Assert.Null(plan.Settings.PickupTime);
        Assert.Null(plan.Settings.PickupPlace);
        Assert.Equal((Guid?)TaxiId, plan.Days[0].Taxi!.TaxiId);
        Assert.Null(plan.Days[0].Taxi!.Booking);
        Assert.Null(plan.Days[0].Taxi!.IsOwn);
        Assert.Null(plan.Days[0].Stay!.Latitude);
        Assert.Null(plan.Days[0].Stay!.Longitude);
        Assert.Null(plan.Days[0].StayShortlist);
        Assert.Null(plan.Days[0].TaxiShortlist);
    }

    [Fact]
    public void RoundTrip_V3_KeepsBookingAndStayCoordinates()
    {
        var sample = Sample();
        sample.Settings.Adults = 4;
        sample.Settings.Kids = 2;
        sample.Settings.Rooms = 2;
        sample.Settings.Needs = [TripNeeds.NoStairs, TripNeeds.VegFood];
        sample.Settings.PickupTime = "08:30";
        sample.Settings.PickupPlace = "Mallital";
        var booking = new TripTaxiBooking
        {
            AgreedFare = 3800, TollExtra = true, PickupTime = "08:30", PickupPlace = "Mallital", Days = [1, 2],
            BookedAt = new DateTime(2026, 10, 28, 10, 15, 0, DateTimeKind.Utc),
        };
        sample.Days[0].Taxi!.Booking = booking;
        sample.Days[0].Taxi!.Fare = 1900;
        sample.Days[1].Taxi = new TripTaxiChoice { TaxiId = TaxiId, Name = "Ramesh", Fare = 1900, Booking = booking };
        sample.Days[0].Stay = new TripStayChoice { StayId = Guid.NewGuid(), Name = "Deodar Homestay", PricePerNight = 3800, Latitude = 29.38, Longitude = 79.46 };

        var json = JsonSerializer.Serialize(sample, TripJson.Options);
        var plan = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;

        Assert.Equal(4, plan.Version);
        Assert.Equal((int?)4, plan.Settings.Adults);
        Assert.Equal((int?)2, plan.Settings.Kids);
        Assert.Equal((int?)2, plan.Settings.Rooms);
        Assert.Equal(new[] { TripNeeds.NoStairs, TripNeeds.VegFood }, plan.Settings.Needs);
        Assert.Equal("08:30", plan.Settings.PickupTime);
        Assert.Equal("Mallital", plan.Settings.PickupPlace);
        foreach (var day in plan.Days)
        {
            var b = day.Taxi!.Booking!;
            Assert.Equal(3800, b.AgreedFare);
            Assert.True(b.TollExtra);
            Assert.Equal("08:30", b.PickupTime);
            Assert.Equal("Mallital", b.PickupPlace);
            Assert.Equal(new[] { 1, 2 }, b.Days);
            Assert.Equal(new DateTime(2026, 10, 28, 10, 15, 0, DateTimeKind.Utc), b.BookedAt);
        }
        Assert.Equal((double?)29.38, plan.Days[0].Stay!.Latitude);
        Assert.Equal((double?)79.46, plan.Days[0].Stay!.Longitude);
    }

    [Fact]
    public void EndDate_IsStartPlusDaysMinusOne()
    {
        var plan = Sample();
        plan.Settings.StartDate = new DateOnly(2026, 11, 1);
        Assert.Equal(new DateOnly(2026, 11, 2), plan.EndDate);

        plan.Days.RemoveAt(1);
        Assert.Equal(new DateOnly(2026, 11, 1), plan.EndDate);

        plan.Settings.StartDate = null;
        Assert.Null(plan.EndDate);
    }

    [Fact]
    public void Serialize_OmitsReservedNulls()
    {
        var json = JsonSerializer.Serialize(Sample(), TripJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var day1 = root.GetProperty("days")[0];
        var taxi = day1.GetProperty("taxi");
        var settings = root.GetProperty("settings");

        Assert.False(root.TryGetProperty("endDate", out _));
        Assert.False(taxi.TryGetProperty("isOwn", out _));
        Assert.False(taxi.TryGetProperty("ownPhone", out _));
        Assert.False(taxi.TryGetProperty("note", out _));
        Assert.False(taxi.TryGetProperty("wholeTrip", out _));
        Assert.False(taxi.TryGetProperty("booking", out _));
        Assert.False(day1.TryGetProperty("stayShortlist", out _));
        Assert.False(day1.TryGetProperty("taxiShortlist", out _));
        Assert.False(settings.TryGetProperty("adults", out _));
        Assert.False(settings.TryGetProperty("needs", out _));
        Assert.False(settings.TryGetProperty("pickupTime", out _));
        Assert.False(settings.TryGetProperty("maxDays", out _));
        Assert.False(root.TryGetProperty("checkOutDate", out _));
    }

    [Fact]
    public void CheckOutDate_IsTheMorningAfterTheLastDay()
    {
        var plan = Sample();
        plan.Settings.StartDate = new DateOnly(2026, 11, 1);
        Assert.Equal(new DateOnly(2026, 11, 2), plan.EndDate);
        Assert.Equal(new DateOnly(2026, 11, 3), plan.CheckOutDate);

        plan.Settings.StartDate = null;
        Assert.Null(plan.CheckOutDate);
    }

    [Fact]
    public void OwnStayAndTaxi_RoundTripWithoutIdKey()
    {
        var plan = Sample();
        plan.Days[0].Stay = new TripStayChoice { IsOwn = true, Name = "Hotel Alka", OwnPhone = "9876543210" };
        plan.Days[0].Taxi = new TripTaxiChoice { IsOwn = true, Name = "Mohan", Fare = 1800 };
        plan.Days[1].Stay = new TripStayChoice { Skipped = true, Name = "No stay tonight" };
        plan.Settings.MaxDays = 2;

        var json = JsonSerializer.Serialize(plan, TripJson.Options);
        using (var doc = JsonDocument.Parse(json))
        {
            var day1 = doc.RootElement.GetProperty("days")[0];
            Assert.False(day1.GetProperty("stay").TryGetProperty("stayId", out _));
            Assert.False(day1.GetProperty("taxi").TryGetProperty("taxiId", out _));
            Assert.True(doc.RootElement.GetProperty("days")[1].GetProperty("stay").GetProperty("skipped").GetBoolean());
            Assert.Equal(2, doc.RootElement.GetProperty("settings").GetProperty("maxDays").GetInt32());
        }

        var back = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;
        Assert.Null(back.Days[0].Stay!.StayId);
        Assert.True(back.Days[0].Stay!.IsOwn);
        Assert.Equal(1800, back.Days[0].Taxi!.Fare);
        Assert.True(back.Days[1].Stay!.Skipped);
        Assert.Equal(2, back.Settings.MaxDays);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public void EmptyIdText_ReadsAsNull(string idJson)
    {
        var choice = JsonSerializer.Deserialize<TripStayChoice>("{\"stayId\":" + idJson + ",\"name\":\"Hotel Alka\",\"isOwn\":true}", TripJson.Options)!;
        Assert.Null(choice.StayId);
    }

    [Fact]
    public void Steps_RoundTrip()
    {
        var journeyId = Guid.NewGuid();
        var plan = Sample();
        plan.Steps = new TripSteps
        {
            StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 },
            GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = journeyId, Date = new DateOnly(2026, 11, 1) },
            ComingBack = new TripStep { Status = TripStepStatuses.Own, Note = "Own car, leaving 6 am" },
        };

        var json = JsonSerializer.Serialize(plan, TripJson.Options);
        using (var doc = JsonDocument.Parse(json))
        {
            var steps = doc.RootElement.GetProperty("steps");
            Assert.Equal("Delhi", steps.GetProperty("startFrom").GetProperty("name").GetString());
            Assert.Equal(28.61, steps.GetProperty("startFrom").GetProperty("lat").GetDouble());
            Assert.Equal(77.21, steps.GetProperty("startFrom").GetProperty("lon").GetDouble());
            Assert.Equal("chosen", steps.GetProperty("gettingThere").GetProperty("status").GetString());
            Assert.Equal("2026-11-01", steps.GetProperty("gettingThere").GetProperty("date").GetString());
            Assert.False(steps.GetProperty("comingBack").TryGetProperty("journeyId", out _));
        }

        var back = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;
        Assert.Equal(TripPlan.CurrentVersion, back.Version);
        Assert.Equal(4, back.Version);
        Assert.Equal("Delhi", back.Steps!.StartFrom!.Name);
        Assert.Equal(journeyId, back.Steps.GettingThere!.JourneyId);
        Assert.Equal(new DateOnly(2026, 11, 1), back.Steps.GettingThere.Date);
        Assert.Equal(TripStepStatuses.Own, back.Steps.ComingBack!.Status);
        Assert.Equal("Own car, leaving 6 am", back.Steps.ComingBack.Note);
        Assert.Null(back.Steps.ComingBack.JourneyId);
    }

    [Fact]
    public void OldDocumentWithoutSteps_ReadsStepsNull()
    {
        var json = JsonSerializer.Serialize(Sample(), TripJson.Options);
        Assert.DoesNotContain("\"steps\"", json);

        var plan = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;
        Assert.Null(plan.Steps);
    }

    [Fact]
    public void StepJourneyIdEmptyText_ReadsNull()
    {
        var steps = JsonSerializer.Deserialize<TripSteps>(
            "{\"gettingThere\":{\"status\":\"notStarted\",\"journeyId\":\"\"},\"comingBack\":{\"journeyId\":null}}", TripJson.Options)!;

        Assert.Null(steps.GettingThere!.JourneyId);
        Assert.Null(steps.ComingBack!.JourneyId);
        Assert.Null(steps.ComingBack.Status);
    }

    [Fact]
    public void SkippedTaxiDay_RoundTripWithoutIdKey()
    {
        var plan = Sample();
        plan.Days[1].Taxi = new TripTaxiChoice { Skipped = true, Name = "No taxi this day" };

        var json = JsonSerializer.Serialize(plan, TripJson.Options);
        using (var doc = JsonDocument.Parse(json))
        {
            var taxi = doc.RootElement.GetProperty("days")[1].GetProperty("taxi");
            Assert.True(taxi.GetProperty("skipped").GetBoolean());
            Assert.False(taxi.TryGetProperty("taxiId", out _));
        }

        var back = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;
        Assert.True(back.Days[1].Taxi!.Skipped);
        Assert.Null(back.Days[1].Taxi!.TaxiId);
        Assert.Null(back.Days[0].Taxi!.Skipped);
    }

    // ---- plan v4: legs ----

    private static readonly Guid DistrictA = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static Trip Row(string planJson) => new()
    {
        Id = Guid.NewGuid(),
        CityKey = 210000123,
        CityName = "Haridwar",
        CityLatitude = 29.95,
        CityLongitude = 78.16,
        DistrictId = DistrictA,
        PlanJson = planJson,
    };

    private const string V3Haridwar =
        "{\"version\":3,\"cityKey\":210000123,\"cityName\":\"Haridwar\",\"centreLatitude\":29.95,\"centreLongitude\":78.16,"
        + "\"days\":[{\"day\":1,\"spots\":[]},{\"day\":2,\"spots\":[]}],\"settings\":{\"dayLimitMinutes\":450}}";

    private static TripPlan TwoLegs() => new()
    {
        CityKey = 210000123,
        CityName = "Haridwar",
        Legs =
        [
            new TripCityLeg { Index = 1, CityKey = 210000123, CityName = "Haridwar", DistrictId = DistrictA, Latitude = 29.95, Longitude = 78.16 },
            new TripCityLeg
            {
                Index = 2, CityKey = 210000456, CityName = "Badrinath", Latitude = 30.74, Longitude = 79.49, NotReady = true,
                Arrival = new TripTravel
                {
                    FromName = "Haridwar", How = TripTravelHows.Bus, OwnName = "Shatabdi 12017", LeavesAt = "06:45",
                    RoadMinutes = TripTravelRoad.WholeDay,
                },
            },
        ],
        Days =
        {
            new TripDay { Day = 1, Leg = 1 },
            new TripDay { Day = 2, Leg = 2 },
            new TripDay { Day = 3, Leg = 2 },
        },
        Settings = new TripSettings { StartDate = new DateOnly(2026, 11, 1) },
    };

    [Fact]
    public void V3Document_ReadsLegsNull_ThenNormalizeGivesOneLegFromTheRow()
    {
        var parsed = TripPlanReader.Parse(V3Haridwar);
        Assert.Null(parsed.Legs);
        Assert.All(parsed.Days, d => Assert.Null(d.Leg));

        var plan = TripPlanReader.Read(Row(V3Haridwar));

        var leg = Assert.Single(plan.Legs!);
        Assert.Equal(1, leg.Index);
        Assert.Equal(210000123L, leg.CityKey);
        Assert.Equal("Haridwar", leg.CityName);
        Assert.Equal((Guid?)DistrictA, leg.DistrictId);
        Assert.Equal(29.95, leg.Latitude);
        Assert.Null(leg.Arrival);
        Assert.All(plan.Days, d => Assert.Equal(1, d.Leg));
        Assert.Equal(new List<string> { "Haridwar" }, plan.LegCities);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"version\":3,\"cityKey\":0,\"days\":[]}")]
    public void UnparseableOrCitylessDocument_ReadsAsTheRowCity(string json)
    {
        var plan = TripPlanReader.Read(Row(json));

        var leg = Assert.Single(plan.Legs!);
        Assert.Equal(210000123L, leg.CityKey);
        Assert.Equal("Haridwar", leg.CityName);
        Assert.Equal(78.16, leg.Longitude);
        Assert.Equal(210000123L, plan.CityKey);
        Assert.Equal("Haridwar", plan.CityName);
        Assert.Equal((Guid?)DistrictA, plan.DistrictId);
    }

    [Fact]
    public void V4TwoLegs_RoundTripKeepsLegsDayLegAndArrival()
    {
        var json = JsonSerializer.Serialize(TwoLegs(), TripJson.Options);
        using (var doc = JsonDocument.Parse(json))
        {
            var legs = doc.RootElement.GetProperty("legs");
            Assert.Equal(2, legs.GetArrayLength());
            Assert.Equal("Badrinath", legs[1].GetProperty("cityName").GetString());
            Assert.True(legs[1].GetProperty("notReady").GetBoolean());
            Assert.Equal("own", legs[1].GetProperty("arrival").GetProperty("mode").GetString());
            Assert.False(legs[0].TryGetProperty("arrival", out _));
            Assert.False(legs[0].TryGetProperty("notReady", out _));
            Assert.Equal(2, doc.RootElement.GetProperty("days")[1].GetProperty("leg").GetInt32());
        }

        var back = TripPlanReader.Parse(json);
        back.Normalize();

        Assert.Equal(4, back.Version);
        Assert.Equal(2, back.Legs!.Count);
        Assert.Equal(new[] { 1, 2, 2 }, back.Days.Select(d => d.Leg!.Value));
        var arrival = back.Legs[1].Arrival!;
        Assert.Equal(TripTravelModes.Own, arrival.Mode);
        Assert.Equal("Haridwar", arrival.FromName);
        Assert.Equal(TripTravelHows.Bus, arrival.How);
        Assert.Equal("Shatabdi 12017", arrival.OwnName);
        Assert.Equal("06:45", arrival.LeavesAt);
        Assert.Equal((int?)450, arrival.RoadMinutes);
        Assert.Equal(new List<string> { "Haridwar", "Badrinath" }, back.LegCities);
    }

    [Fact]
    public void EmptyLegs_ReadAsNone()
    {
        var plan = TripPlanReader.Read(Row(V3Haridwar.Replace("\"days\"", "\"legs\":[],\"days\"")));

        Assert.Single(plan.Legs!);
        Assert.Equal("Haridwar", plan.Legs![0].CityName);
    }

    [Fact]
    public void V4FirstLegWithoutDistrict_TakesTheRowsDistrict()
    {
        var plan = TwoLegs();
        plan.Legs![0].DistrictId = null;

        var back = TripPlanReader.Read(Row(JsonSerializer.Serialize(plan, TripJson.Options)));

        Assert.Equal((Guid?)DistrictA, back.Legs![0].DistrictId);
        Assert.Equal((Guid?)DistrictA, back.DistrictId);
        Assert.Null(back.Legs[1].DistrictId);
    }

    [Fact]
    public void DayWithoutLeg_TakesThePreviousDaysLeg()
    {
        var plan = TwoLegs();
        plan.Days.Add(new TripDay { Day = 4 });
        plan.Days[0].Leg = null;

        plan.Normalize();

        Assert.Equal(new[] { 1, 2, 2, 2 }, plan.Days.Select(d => d.Leg!.Value));
    }

    [Fact]
    public void DecreasingOrOutOfRangeLeg_IsRaisedOrClamped()
    {
        var plan = TwoLegs();
        plan.Days[2].Leg = 1;
        plan.Days.Add(new TripDay { Day = 4, Leg = 9 });

        plan.Normalize();

        Assert.Equal(new[] { 1, 2, 2, 2 }, plan.Days.Select(d => d.Leg!.Value));
    }

    [Fact]
    public void Normalize_CopiesTheCityFieldsFromTheFirstLeg_AndReindexes()
    {
        var plan = TwoLegs();
        plan.CityKey = 999;
        plan.CityName = "Stale";
        plan.Legs![0].Index = 7;
        plan.Legs[1].Index = 3;

        plan.Normalize();

        Assert.Equal(210000123L, plan.CityKey);
        Assert.Equal("Haridwar", plan.CityName);
        Assert.Equal((Guid?)DistrictA, plan.DistrictId);
        Assert.Equal((double?)29.95, plan.CentreLatitude);
        Assert.Equal((double?)78.16, plan.CentreLongitude);
        Assert.Equal(new[] { 1, 2 }, plan.Legs.Select(l => l.Index));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var plan = TwoLegs();
        plan.Normalize();
        var once = JsonSerializer.Serialize(plan, TripJson.Options);

        plan.Normalize();

        Assert.Equal(once, JsonSerializer.Serialize(plan, TripJson.Options));
    }

    [Fact]
    public void LegHelpers_FollowTheDays()
    {
        var plan = TwoLegs();
        plan.Normalize();

        Assert.Equal("Badrinath", plan.LegOf(2)!.CityName);
        Assert.Equal(1, plan.LegIndexOf(1));
        Assert.Equal(2, plan.LegIndexOf(3));
        Assert.Equal(1, plan.LegIndexOf(99));
        Assert.Equal(1, plan.LegStart(1));
        Assert.Equal(1, plan.LegEnd(1));
        Assert.Equal(2, plan.LegStart(2));
        Assert.Equal(3, plan.LegEnd(2));
        Assert.Equal(0, plan.LegStart(3));
        Assert.Equal(new[] { 2, 3 }, plan.DaysOf(2).Select(d => d.Day));
        Assert.True(plan.IsTravelDay(2));
        Assert.False(plan.IsTravelDay(1));
        Assert.False(plan.IsTravelDay(3));
    }

    [Fact]
    public void EveryDayHasANight_CheckOutIsAfterTheLastDayOfTheLastLeg()
    {
        var plan = TwoLegs();

        Assert.Equal(new DateOnly(2026, 11, 3), plan.EndDate);
        Assert.Equal(new DateOnly(2026, 11, 4), plan.CheckOutDate);
    }

    [Fact]
    public void Steps_CarriedThroughV4()
    {
        var journeyId = Guid.NewGuid();
        var plan = TwoLegs();
        plan.Steps = new TripSteps
        {
            StartFrom = new TripStartFrom { Name = "Delhi", Lat = 28.61, Lon = 77.21 },
            GettingThere = new TripStep { Status = TripStepStatuses.Chosen, JourneyId = journeyId },
            ComingBack = new TripStep { Status = TripStepStatuses.Own, Note = "Own car" },
        };

        var back = TripPlanReader.Read(Row(JsonSerializer.Serialize(plan, TripJson.Options)));

        Assert.Equal(2, back.Legs!.Count);
        Assert.Equal("Delhi", back.Steps!.StartFrom!.Name);
        Assert.Equal(journeyId, back.Steps.GettingThere!.JourneyId);
        Assert.Equal(TripStepStatuses.Own, back.Steps.ComingBack!.Status);
    }

    [Fact]
    public void LeglessPlan_LegCitiesIsTheCityName()
        => Assert.Equal(new List<string> { "Nainital" }, Sample().LegCities);

    [Fact]
    public void RealId_StillReads()
    {
        var id = Guid.NewGuid();
        var choice = JsonSerializer.Deserialize<TripStayChoice>("{\"stayId\":\"" + id + "\",\"name\":\"Lake View\"}", TripJson.Options)!;
        Assert.Equal(id, choice.StayId);
        Assert.Contains($"\"stayId\":\"{id}\"", JsonSerializer.Serialize(choice, TripJson.Options));
    }
}
