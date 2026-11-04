using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class AtlasClientParseTests
{
    private const string SpotsBody = """
        {"status":"200","code":200,"timestamp":1793613600,"error":null,
         "data":{"cityKey":210000001,"cityName":"Nainital","districtName":"Nainital","stateName":"Uttarakhand",
          "latitude":29.3919,"longitude":79.4542,"imageUrl":null,"imageCredit":null,"imageLicense":null,
          "tripEnabled":true,"isPopularDestination":true,"status":"Ready",
          "generatedAt":"2026-11-02T10:00:00Z","spotCount":1,
          "spots":[{"id":"osm:N123","source":"seed","name":"Naini Lake","displayName":"Naini Lake",
            "altNames":["Naini Tal","Nainī Tāl"],"description":"Lake",
            "latitude":29.39,"longitude":79.45,"category":"lake","area":"Mall Road","visitMinutes":60,"isFeatured":true,
            "rank":1,"imageUrl":"https://img/x.jpg","imageCredit":"A","imageLicense":"CC BY-SA 4.0"}]}}
        """;

    private const string CityPageBody = """
        {"status":"200","code":200,"timestamp":1793613600,"error":null,
         "data":{"items":[{"cityKey":210000002,"name":"Rishikesh","districtId":"5c1f0e3a-1b2c-4d5e-8f60-123456789abc",
           "districtName":"Dehradun","stateName":"Uttarakhand",
           "latitude":30.1,"longitude":78.3,"imageUrl":null,"imageCredit":null,"imageLicense":null,
           "isTripEnabled":true,"isPopularDestination":true,"status":"Ready","spotCount":37,"updatedAt":"2026-11-03T08:00:00Z"}],
          "hasMore":false,"catalogUpdatedAt":"2026-11-03T08:00:00Z","catalogCount":42}}
        """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AtlasClient Client(HttpMessageHandler handler, string? baseUrl = "https://atlas.test", string? apiKey = "k123")
        => new(new HttpClient(handler), Options.Create(new AtlasOptions { BaseUrl = baseUrl, ApiKey = apiKey }),
            NullLogger<AtlasClient>.Instance);

    [Fact]
    public void ParseData_CitySpots()
    {
        var spots = AtlasClient.ParseData<AtlasCitySpots>(SpotsBody);

        Assert.Equal(210000001L, spots.CityKey);
        Assert.Equal("Nainital", spots.CityName);
        Assert.Equal("Nainital", spots.DistrictName);
        Assert.Equal("Uttarakhand", spots.StateName);
        Assert.Equal(29.3919, spots.Latitude);
        Assert.Equal(79.4542, spots.Longitude);
        Assert.True(spots.IsPopularDestination);
        Assert.Equal("Ready", spots.Status);
        Assert.True(spots.TripEnabled);
        Assert.Equal(new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), spots.GeneratedAt?.ToUniversalTime());
        var spot = Assert.Single(spots.Spots);
        Assert.Equal("osm:N123", spot.Id);
        Assert.Equal("seed", spot.Source);
        Assert.Equal(new[] { "Naini Tal", "Nainī Tāl" }, spot.AltNames);
        Assert.Equal("Mall Road", spot.Area);
        Assert.Equal(60, spot.VisitMinutes);
        Assert.True(spot.IsFeatured);
        Assert.Equal("CC BY-SA 4.0", spot.ImageLicense);
    }

    [Theory]
    [InlineData("""{"status":"200","code":200,"data":null}""")]
    [InlineData("""{"status":"200","code":200}""")]
    [InlineData("""[]""")]
    [InlineData("not json")]
    public void ParseData_BadBody_Throws(string body)
        => Assert.Throws<AtlasUnavailableException>(() => AtlasClient.ParseData<AtlasCitySpots>(body));

    [Fact]
    public async Task GetCitySpots_SendsKeyAndPath()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SpotsBody);

        var spots = await Client(handler).GetCitySpotsAsync(110000001L);

        Assert.NotNull(spots);
        Assert.Equal("https://atlas.test/api/v1/cities/110000001/spots", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("k123", Assert.Single(handler.LastRequest.Headers.GetValues("X-Atlas-Api-Key")));
    }

    [Fact]
    public async Task GetCitySpots_BaseUrlWithTrailingSlash_KeepsPath()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SpotsBody);

        await Client(handler, baseUrl: " https://atlas.test/ ").GetCitySpotsAsync(110000001L);

        Assert.Equal("https://atlas.test/api/v1/cities/110000001/spots", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetCitySpots_NotFound_ReturnsNull()
        => Assert.Null(await Client(new StubHandler(HttpStatusCode.NotFound, """{"error":{"message":"x","type":"CityNotFound"}}"""))
            .GetCitySpotsAsync(1L));

    [Fact]
    public async Task GetCitySpots_ServerError_Throws()
        => await Assert.ThrowsAsync<AtlasUnavailableException>(() =>
            Client(new StubHandler(HttpStatusCode.InternalServerError, "{}")).GetCitySpotsAsync(1L));

    [Fact]
    public async Task NotConfigured_Throws_WithoutRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, SpotsBody);
        var client = Client(handler, apiKey: null);

        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<AtlasUnavailableException>(() => client.GetCitySpotsAsync(1L));
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public void ParseData_CityPage()
    {
        var page = AtlasClient.ParseData<AtlasCityPage>(CityPageBody);

        var city = Assert.Single(page.Items);
        Assert.Equal(210000002L, city.CityKey);
        Assert.Equal(Guid.Parse("5c1f0e3a-1b2c-4d5e-8f60-123456789abc"), city.DistrictId);
        Assert.True(city.IsPopularDestination);
        Assert.Equal("Rishikesh", city.Name);
        Assert.Equal("Dehradun", city.DistrictName);
        Assert.Equal("Uttarakhand", city.StateName);
        Assert.Equal(30.1, city.Latitude);
        Assert.Equal(78.3, city.Longitude);
        Assert.True(city.IsTripEnabled);
        Assert.Equal("Ready", city.Status);
        Assert.Equal(37, city.SpotCount);
        Assert.False(page.HasMore);
        Assert.Equal(new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc), page.CatalogUpdatedAt.ToUniversalTime());
        Assert.Equal(42, page.CatalogCount);
    }

    [Fact]
    public async Task SearchCities_SendsKeyAndQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK, CityPageBody);

        await Client(handler).SearchCitiesAsync("rish", 2, 200);

        Assert.Equal("https://atlas.test/api/v1/cities?page=2&pageSize=200&ready=false&q=rish", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("k123", Assert.Single(handler.LastRequest.Headers.GetValues("X-Atlas-Api-Key")));
    }

    [Fact]
    public async Task SearchCities_BlankQ_OmitsQ()
    {
        var handler = new StubHandler(HttpStatusCode.OK, CityPageBody);

        await Client(handler).SearchCitiesAsync("  ", 1, 50, readyOnly: true);

        Assert.Equal("https://atlas.test/api/v1/cities?page=1&pageSize=50&ready=true", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SearchCities_PopularAndState_AppendsParams()
    {
        var handler = new StubHandler(HttpStatusCode.OK, CityPageBody);
        var stateId = Guid.Parse("5c1f0e3a-1b2c-4d5e-8f60-123456789abc");

        await Client(handler).SearchCitiesAsync(null, 1, 200, popularOnly: true, stateId: stateId);

        Assert.Equal(
            "https://atlas.test/api/v1/cities?page=1&pageSize=200&ready=false&popular=true&stateId=5c1f0e3a-1b2c-4d5e-8f60-123456789abc",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SearchCities_Defaults_OmitPopularAndState()
    {
        var handler = new StubHandler(HttpStatusCode.OK, CityPageBody);

        await Client(handler).SearchCitiesAsync(null, 1, 200);

        Assert.Equal("https://atlas.test/api/v1/cities?page=1&pageSize=200&ready=false", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SearchCities_NotFound_Throws()
        => await Assert.ThrowsAsync<AtlasUnavailableException>(() =>
            Client(new StubHandler(HttpStatusCode.NotFound, "{}")).SearchCitiesAsync(null, 1, 200));

    [Fact]
    public async Task GetCatalogVersion_Parses()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"status":"200","code":200,"data":{"updatedAt":"2026-11-03T08:00:00Z","count":42}}""");

        var version = await Client(handler).GetCatalogVersionAsync();

        Assert.Equal("https://atlas.test/api/v1/cities/version", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc), version.UpdatedAt.ToUniversalTime());
        Assert.Equal(42, version.Count);
    }
}
