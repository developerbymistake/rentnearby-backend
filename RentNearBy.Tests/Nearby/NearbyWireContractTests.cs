using System.Text.Json;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Nearby;

// Wire keys the app parses (CONTRACT_NEARBY). The app side has a matching sample in test/nearby/nearby_wire_contract_test.dart.
public class NearbyWireContractTests
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    static HashSet<string> Keys<T>(T dto)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web));
        return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
    }

    static readonly string[] StayBase = ["id", "userId", "name", "stayTypeId", "stayTypeName", "thumbnailUrl", "cityName", "districtName",
        "minPricePerNight", "mealsChip", "roomNameChip", "priorityRank", "isFeatured", "createdAt"];

    static readonly string[] TaxiBase = ["id", "driverName", "vehicleTypeName", "vehicleCategory", "seats", "plateDisplay", "cityId", "cityName",
        "districtName", "minFare", "routeCount", "localDayRate", "localDayKm", "tripSeatsFit", "tripCars", "tripDayCost", "tripExtraKm",
        "tripPickupKm", "tripReplyLabel", "matchedFare", "matchedFromName", "matchedToName", "matchedTripType", "matchReversed",
        "priorityRank", "isFeatured", "createdAt"];

    static readonly string[] JobBase = ["id", "stayListingId", "stayName", "stayTypeName", "coverPhotoUrl", "title", "departmentName", "roleName",
        "jobTypeName", "shiftTypeName", "salaryTypeName", "minimumExperienceYears", "minSalary", "maxSalary", "districtId", "districtName",
        "cityName", "createdAt"];

    [Fact]
    public void Header_keys()
        => Assert.Equal(new[] { "centreCityName", "centreStateName", "centreCityKm", "livesIn", "farAway", "nearestKm", "citiesUnavailable" }.ToHashSet(),
            Keys(new NearbyHeaderDto()));

    [Fact]
    public void Page_keys()
        => Assert.Equal(new[] { "header", "featured", "items", "hasMore", "nextToken" }.ToHashSet(), Keys(new NearbyPageDto<StayFeedItemDto>()));

    [Fact]
    public void Stay_item_keys()
        => Assert.Equal(StayBase.Concat(["stateName", "distanceKm", "labelKey", "isNear"]).ToHashSet(), Keys(new StayFeedItemDto()));

    [Fact]
    public void Taxi_item_keys()
        => Assert.Equal(TaxiBase.Concat(["stateName", "distanceKm", "labelKey", "isNear", "listedForRoute"]).ToHashSet(), Keys(new TaxiFeedItemDto()));

    [Fact]
    public void Taxi_tier_and_route_feed_keys()
    {
        Assert.Equal(new[] { "key", "headerKey", "header", "items", "hasMore", "nextToken", "collapsed" }.ToHashSet(), Keys(new TaxiTierDto()));
        Assert.Equal(new[] { "header", "tiers" }.ToHashSet(), Keys(new TaxiRouteFeedDto()));
    }

    [Fact]
    public void Job_item_keys()
        => Assert.Equal(JobBase.Concat(["stateName", "cityKey", "distanceKm", "labelKey", "isNear"]).ToHashSet(), Keys(new StayJobFeedItemDto()));

    [Fact]
    public void Home_keys_are_a_superset_of_the_old_feed()
        => Assert.Equal(new[] { "tabs", "header", "stays", "taxis", "recentStays" }.ToHashSet(), Keys(new HomeNearbyFeedDto()));

    [Fact]
    public void Home_tabs_keys()
        => Assert.Equal(new[] { "stays", "jobs", "taxi" }.ToHashSet(), Keys(new HomeFeedTabsDto()));

    [Fact]
    public void Taxi_distance_and_label_values_are_wire_safe()
    {
        var item = new TaxiFeedItemDto { Id = Guid.NewGuid(), DistanceKm = 35, LabelKey = "far", IsNear = false };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(item, Web));
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("distanceKm").ValueKind);
        Assert.Equal("far", doc.RootElement.GetProperty("labelKey").GetString());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("id").ValueKind);
    }

    [Fact]
    public void Job_city_key_is_a_json_number()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new StayJobFeedItemDto { CityKey = 123456789012L }, Web));
        Assert.Equal(123456789012L, doc.RootElement.GetProperty("cityKey").GetInt64());
    }
}
