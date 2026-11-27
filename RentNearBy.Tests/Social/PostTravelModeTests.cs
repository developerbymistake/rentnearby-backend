using System.Text.Json;
using RentNearBy.Core.DTOs.Social;
using RentNearBy.Core.Models.Social;
using RentNearBy.Infrastructure.Services.Social;

namespace RentNearBy.Tests.Social;

// Social next item 1 (owner 2026-11-27): one optional travel mode per public post, known values only, feed filter `travel`.
public class PostTravelModeTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    private static PostFields Fields(string? travelMode) =>
        new(SocialRules.WhenKindWithin, 2, null, 4, JoinGenders.Anyone, 2, "Easy trek with friends", travelMode);

    [Fact]
    public void Known_modes_are_the_four_options()
        => Assert.Equal(new[] { "taxi", "bike", "bus", "train" }, PostTravelModes.All);

    [Theory]
    [InlineData(null)]
    [InlineData("taxi")]
    [InlineData("bike")]
    [InlineData("bus")]
    [InlineData("train")]
    public void Not_set_or_a_known_mode_passes(string? mode)
        => Assert.Null(PublicTripService.FieldError(Fields(mode), Today));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("plane")]
    [InlineData("Bike")]
    [InlineData("public")]
    [InlineData("car")]
    [InlineData("bike,bus")]
    [InlineData("any")]
    public void Unknown_mode_is_refused_on_the_travel_mode_field(string mode)
        => Assert.Equal("travelMode", PublicTripService.FieldError(Fields(mode), Today));

    [Fact]
    public void Earlier_field_errors_still_win()
        => Assert.Equal("days", PublicTripService.FieldError(Fields("plane") with { Days = 0 }, Today));

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("any", true, null)]
    [InlineData("taxi", true, "taxi")]
    [InlineData("bike", true, "bike")]
    [InlineData("bus", true, "bus")]
    [InlineData("train", true, "train")]
    [InlineData("public", false, null)]
    [InlineData("car", false, null)]
    [InlineData("plane", false, null)]
    [InlineData("Taxi", false, null)]
    [InlineData("", false, null)]
    public void Feed_travel_filter_maps_to_a_mode(string? travel, bool valid, string? code)
    {
        Assert.Equal(valid, PostTravelModes.TryFilter(travel, out var got));
        Assert.Equal(code, got);
    }

    [Fact]
    public void Every_mode_fits_the_column()
        => Assert.All(PostTravelModes.All, m => Assert.True(m.Length <= PostTravelModes.MaxLength));

    [Fact]
    public void Public_trip_json_names_the_field_travel_mode()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Contains("\"travelMode\":\"bike\"", JsonSerializer.Serialize(new PublicTripDto { TravelMode = PostTravelModes.Bike }, web));
        Assert.Contains("\"travelMode\":null", JsonSerializer.Serialize(new PublicTripDto(), web));
        var create = JsonSerializer.Deserialize<CreatePublicTripRequest>("""{"travelMode":"taxi"}""", web)!;
        Assert.Equal(PostTravelModes.Taxi, create.TravelMode);
        Assert.Null(JsonSerializer.Deserialize<UpdatePublicPostRequest>("{}", web)!.TravelMode);
    }

    [Fact]
    public void Reader_maps_the_stored_mode_and_not_set_stays_null()
    {
        var now = new DateTime(2026, 11, 27, 6, 0, 0, DateTimeKind.Utc);
        var row = new PublicTripReader.Row
        {
            TripId = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(), OwnerName = "Riya Sharma", PostText = "Easy treks.", PostDays = 3, Seats = 2,
            PublishedAt = now.AddDays(-1), PostWindowMonths = 2, CityNames = ["Manali"], IsPublic = true, Live = true, TravelMode = PostTravelModes.Train,
        };
        var me = Guid.NewGuid();
        Assert.Equal(PostTravelModes.Train, PublicTripReader.ToDto(row, me, now, null, SocialGenders.Male).TravelMode);
        row.TravelMode = null;
        Assert.Null(PublicTripReader.ToDto(row, me, now, null, SocialGenders.Male).TravelMode);
    }
}
