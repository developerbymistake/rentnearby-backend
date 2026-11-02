using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class AtlasClientParseTests
{
    private const string ExportBody = """
        {"status":"200","code":200,"timestamp":1793613600,"error":null,
         "data":{"items":[
           {"cityKey":110000001,"seedIndex":0,"name":"Nainital","districtId":"6f1c2a52-6a3c-4a0e-9b3e-3f1f7d2b9a10","districtName":"Nainital",
            "stateName":"Uttarakhand","latitude":29.3919,"longitude":79.4542,"seedStatus":"Ok","imageUrl":null,"isTripEnabled":true},
           {"cityKey":110000003,"seedIndex":1,"name":"Bhowali","districtId":null,"districtName":"Nainital",
            "stateName":"Uttarakhand","latitude":29.38,"longitude":79.51,"seedStatus":"Duplicate","imageUrl":null,"isTripEnabled":false}],
          "hasMore":true,"nextAfter":110000003}}
        """;

    private const string SpotsBody = """
        {"status":"200","code":200,"timestamp":1793613600,"error":null,
         "data":{"cityKey":110000001,"cityName":"Nainital","districtName":"Nainital","stateName":"Uttarakhand",
          "latitude":29.3919,"longitude":79.4542,"imageUrl":null,"tripEnabled":true,"status":"Ready",
          "generatedAt":"2026-11-02T10:00:00Z","spotCount":1,
          "spots":[{"id":"wiki:123","source":"merged","name":"Naini Lake","displayName":"Naini Lake","description":"Lake",
            "latitude":29.39,"longitude":79.45,"category":"lake","area":"Mall Road","visitMinutes":60,"isFeatured":true,
            "rank":1,"imageUrl":"https://img/x.jpg","imageCredit":"A","imageLicense":"CC BY-SA 4.0"}]}}
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
    public void ParseData_ExportPage()
    {
        var page = AtlasClient.ParseData<AtlasExportPage>(ExportBody);

        Assert.True(page.HasMore);
        Assert.Equal(110000003L, page.NextAfter);
        Assert.Equal(2, page.Items.Count);
        var first = page.Items[0];
        Assert.Equal(110000001L, first.CityKey);
        Assert.Equal(0, first.SeedIndex);
        Assert.Equal("Nainital", first.Name);
        Assert.Equal("Nainital", first.DistrictName);
        Assert.Equal(29.3919, first.Latitude);
        Assert.Equal("Ok", first.SeedStatus);
        Assert.True(first.IsTripEnabled);
        Assert.Null(page.Items[1].DistrictId);
        Assert.Equal("Duplicate", page.Items[1].SeedStatus);
    }

    [Fact]
    public void ParseData_CitySpots()
    {
        var spots = AtlasClient.ParseData<AtlasCitySpots>(SpotsBody);

        Assert.Equal(110000001L, spots.CityKey);
        Assert.Equal("Nainital", spots.CityName);
        Assert.Equal("Ready", spots.Status);
        Assert.True(spots.TripEnabled);
        Assert.Equal(new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc), spots.GeneratedAt?.ToUniversalTime());
        var spot = Assert.Single(spots.Spots);
        Assert.Equal("wiki:123", spot.Id);
        Assert.Equal("merged", spot.Source);
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
    public async Task ExportCities_SendsQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ExportBody);

        await Client(handler).ExportCitiesAsync(110000001L, 5000);

        Assert.Equal("https://atlas.test/api/v1/export/cities?after=110000001&limit=5000", handler.LastRequest!.RequestUri!.ToString());
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
}
