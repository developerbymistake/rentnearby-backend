using System.Text.Json;

namespace RentNearBy.Tests.Trips;

public class TripPlanJsonTests
{
    private static readonly Guid CityId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid DistrictId = Guid.Parse("66666666-7777-8888-9999-000000000000");
    private static readonly Guid TaxiId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private static TripPlan Sample() => new()
    {
        CityId = CityId,
        DistrictId = DistrictId,
        CityKey = 110000001,
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

        Assert.Equal(1, plan.Version);
        Assert.Equal(CityId, plan.CityId);
        Assert.Equal(DistrictId, plan.DistrictId);
        Assert.Equal((long?)110000001, plan.CityKey);
        Assert.Equal("Nainital", plan.CityName);
        Assert.Equal(2, plan.Days.Count);
        Assert.Equal("Mall Road", plan.Days[0].Spots[0].Name);
        Assert.Equal(TaxiId, plan.Days[0].Taxi!.TaxiId);
        Assert.Equal((int?)1800, plan.Days[0].Taxi!.Fare);
        Assert.Equal("Sedan", plan.Days[0].Taxi!.VehicleTypeName);
        Assert.Equal(new[] { 1 }, plan.Days[1].Spots[0].ClosedDays);
        Assert.Equal(new DateOnly(2026, 11, 2), plan.Settings.StartDate);
    }

    [Fact]
    public void Serialize_CamelCaseNames()
    {
        var json = JsonSerializer.Serialize(Sample(), TripJson.Options);

        Assert.Contains("\"cityId\":", json);
        Assert.Contains("\"centreLatitude\":", json);
        Assert.Contains("\"visitMinutes\":", json);
        Assert.Contains("\"dayLimitMinutes\":", json);
        Assert.Contains("\"startDate\":\"2026-11-02\"", json);
        Assert.Contains("\"vehicleTypeName\":\"Sedan\"", json);
        Assert.DoesNotContain("\"CityId\"", json);
    }

    [Fact]
    public void Serialize_OmitsNulls()
    {
        var json = JsonSerializer.Serialize(Sample(), TripJson.Options);
        using var doc = JsonDocument.Parse(json);
        var day2 = doc.RootElement.GetProperty("days")[1];
        var spot1 = doc.RootElement.GetProperty("days")[0].GetProperty("spots")[0];

        Assert.False(day2.TryGetProperty("taxi", out _));
        Assert.False(day2.TryGetProperty("stay", out _));
        Assert.False(spot1.TryGetProperty("imageUrl", out _));
        Assert.False(spot1.TryGetProperty("closedDays", out _));
    }

    [Fact]
    public void Deserialize_CaseInsensitive()
    {
        var json = $"{{\"Version\":1,\"CityId\":\"{CityId}\",\"CITYNAME\":\"Nainital\",\"Days\":[{{\"Day\":1,\"Spots\":[{{\"Id\":\"osm:N1\",\"Name\":\"Lake\",\"VisitMinutes\":60}}]}}],\"Settings\":{{\"DayLimitMinutes\":400}}}}";

        var plan = JsonSerializer.Deserialize<TripPlan>(json, TripJson.Options)!;

        Assert.Equal(CityId, plan.CityId);
        Assert.Equal("Nainital", plan.CityName);
        Assert.Equal("Lake", plan.Days[0].Spots[0].Name);
        Assert.Equal(400, plan.Settings.DayLimitMinutes);
    }
}
