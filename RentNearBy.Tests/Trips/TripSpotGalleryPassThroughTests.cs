using System.Text.Json;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Infrastructure.Services;

namespace RentNearBy.Tests.Trips;

public class TripSpotGalleryPassThroughTests
{
    private const long Key = 210000001L;

    private static AtlasCitySpots Ready(params TripSpotDto[] spots) => new()
    {
        CityKey = Key,
        CityName = "Nainital",
        Status = TripSpotStatuses.Ready,
        GeneratedAt = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc),
        Spots = [.. spots],
    };

    private static List<string> Urls(string prefix, int count)
        => Enumerable.Range(1, count).Select(i => $"https://res.cloudinary.com/demo/image/upload/v1/{prefix}{i}.jpg").ToList();

    [Fact]
    public void FiveImagesTwelveGallery_ClipsToThreeAndTen()
    {
        var images = Urls("p", 5);
        var gallery = Urls("g", 12);
        var cached = TripSpotCache.ToCached(Key, Ready(new TripSpotDto { Id = "wiki:1", Images = images, Gallery = gallery }), DateTime.UtcNow);

        var spot = Assert.Single(cached.Spots);
        Assert.Equal(images.Take(3), spot.Images);
        Assert.Equal(images[0], spot.ImageUrl);
        Assert.Equal(10, spot.GalleryCount);
        Assert.Null(spot.Gallery);
        Assert.Equal(gallery.Take(10), cached.Galleries["wiki:1"]);
    }

    [Fact]
    public void ExistingImageUrl_IsKept()
    {
        var cached = TripSpotCache.ToCached(Key,
            Ready(new TripSpotDto { Id = "wiki:1", ImageUrl = "https://img/cover.jpg", Images = ["https://img/a.jpg"] }), DateTime.UtcNow);

        Assert.Equal("https://img/cover.jpg", cached.Spots[0].ImageUrl);
    }

    [Fact]
    public void NoGallery_BlankAndDuplicateUrlsDropped_BlankIdDropped()
    {
        var cached = TripSpotCache.ToCached(Key, Ready(
            new TripSpotDto { Id = "osm:N2", Images = ["https://img/a.jpg", " ", "", "https://img/a.jpg", "https://img/b.jpg"] },
            new TripSpotDto { Id = "  ", Images = ["https://img/z.jpg"] },
            new TripSpotDto { Id = "wiki:3", Gallery = ["", "  ", "https://img/g.jpg", "https://img/g.jpg"] }), DateTime.UtcNow);

        Assert.Equal(new[] { "osm:N2", "wiki:3" }, cached.Spots.Select(s => s.Id));
        Assert.Equal(new[] { "https://img/a.jpg", "https://img/b.jpg" }, cached.Spots[0].Images);
        Assert.Equal(0, cached.Spots[0].GalleryCount);
        Assert.False(cached.Galleries.ContainsKey("osm:N2"));
        Assert.Equal(1, cached.Spots[1].GalleryCount);
        Assert.Equal(new[] { "https://img/g.jpg" }, cached.Galleries["wiki:3"]);
    }

    [Fact]
    public void AtlasGalleryCountWithoutUrls_IsZero()
    {
        var cached = TripSpotCache.ToCached(Key, Ready(new TripSpotDto { Id = "wiki:1", GalleryCount = 7 }), DateTime.UtcNow);

        Assert.Equal(0, cached.Spots[0].GalleryCount);
        Assert.Empty(cached.Galleries);
    }

    [Fact]
    public void NotReady_HasNoGalleries()
    {
        var dto = Ready(new TripSpotDto { Id = "wiki:1", Gallery = Urls("g", 2) });
        dto.Status = TripSpotStatuses.NotReady;

        var cached = TripSpotCache.ToCached(Key, dto, DateTime.UtcNow);

        Assert.Empty(cached.Spots);
        Assert.Empty(cached.Galleries);
    }

    [Fact]
    public void CachedCitySpots_RoundTrip_KeepsGalleries()
    {
        var cached = TripSpotCache.ToCached(Key, Ready(new TripSpotDto { Id = "wiki:1", Images = Urls("p", 1), Gallery = Urls("g", 3) }), DateTime.UtcNow);

        var json = JsonSerializer.Serialize(cached, TripJson.Options);
        var back = JsonSerializer.Deserialize<CachedCitySpots>(json, TripJson.Options)!;

        Assert.Contains("\"galleries\":{\"wiki:1\":", json);
        Assert.Equal(Urls("g", 3), back.Galleries["wiki:1"]);
        Assert.Equal(3, back.Spots[0].GalleryCount);
        Assert.Null(back.Spots[0].Gallery);
    }

    [Fact]
    public void V2ShapedBlob_DeserialisesAndNormalises()
    {
        const string v2 = """
            {"cityKey":210000001,"cityName":"Nainital","status":"Ready","generatedAt":"2026-11-02T10:00:00Z",
             "fetchedAt":"2026-11-02T10:05:00Z",
             "spots":[{"id":"wiki:1","name":"Naini Lake","imageUrl":"https://img/1.jpg",
               "images":["https://img/1.jpg","https://img/2.jpg","https://img/3.jpg","https://img/4.jpg","https://img/5.jpg"]}]}
            """;

        var blob = JsonSerializer.Deserialize<CachedCitySpots>(v2, TripJson.Options)!;
        var result = TripSpotCache.FromLegacy(blob);

        Assert.Equal(TripSpotStatuses.Stale, result.Status);
        Assert.Empty(result.Galleries);
        Assert.Equal(3, result.Spots[0].Images.Count);
        Assert.Equal(0, result.Spots[0].GalleryCount);
    }

    public static TheoryData<string> Serialisers => new("trip", "web");

    [Theory]
    [MemberData(nameof(Serialisers))]
    public void ListPage_HasGalleryCount_NeverGallery(string serialiser)
    {
        var gallery = Urls("gallery-secret-", 4);
        var cached = TripSpotCache.ToCached(Key, Ready(new TripSpotDto { Id = "wiki:1", Images = Urls("p", 2), Gallery = gallery }), DateTime.UtcNow);
        var page = new TripSpotsPageDto
        {
            Items = cached.Spots.Take(20).ToList(),
            Status = cached.Status,
            CityKey = Key,
            Total = cached.Spots.Count,
            GeneratedAt = cached.GeneratedAt,
        };

        var options = serialiser == "trip" ? TripJson.Options : new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(page, options);

        Assert.Contains("\"galleryCount\":4", json);
        Assert.DoesNotContain("\"gallery\":", json);
        Assert.DoesNotContain("gallery-secret-", json);
    }
}
