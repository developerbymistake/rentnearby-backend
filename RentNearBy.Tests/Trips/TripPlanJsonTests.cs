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

        Assert.Equal(3, plan.Version);
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
    }
}
