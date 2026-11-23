using System.Reflection;
using System.Text.Json;
using Mapster;
using RentNearBy.Api.Handlers;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.DTOs.TaxiRequests;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Taxis;

// CONTRACT_BACKEND (card photos) R1, R3, R4, R5, section 9 BE1: plate proof never on a public or seeker DTO, display photo on every card DTO.
public class TaxiPhotoWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Url = "https://res.cloudinary.com/x/image/upload/bakhli/user_1/listing_2/d.jpg";

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([CompletionCounterGuardTests.Root(), .. parts]));

    private static string Method(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var next = src.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? src[start..] : src[start..next];
    }

    private static JsonElement Json(object dto) => JsonDocument.Parse(JsonSerializer.Serialize(dto, dto.GetType(), Web)).RootElement;

    [Fact]
    public void Public_and_slug_dto_have_no_plate_photo()
    {
        var allowed = new HashSet<Type> { typeof(TaxiOwnerDto), typeof(AdminTaxiDto) };
        var leaks = typeof(TaxiPublicDto).Assembly.GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Where(t => t.Namespace?.StartsWith("RentNearBy.Core.DTOs", StringComparison.Ordinal) == true)
            .Where(t => !allowed.Contains(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name.Contains("PlatePhoto", StringComparison.Ordinal))
                .Select(p => $"{t.FullName}.{p.Name}"))
            .ToList();
        Assert.Empty(leaks);
        Assert.Null(typeof(TaxiPublicDto).GetProperty("PlatePhotoUrl"));
        Assert.Null(typeof(TaxiPublicSlugDto).GetProperty("PlatePhotoUrl"));
    }

    [Fact]
    public void Seeker_dtos_carry_displayPhotoUrl()
    {
        object[] dtos =
        [
            new TaxiPublicDto { DisplayPhotoUrl = Url },
            new TaxiPublicSlugDto { DisplayPhotoUrl = Url },
            new TaxiBrowseDto { DisplayPhotoUrl = Url },
            new TaxiFeedItemDto { DisplayPhotoUrl = Url },
            new WayDriverDto { DisplayPhotoUrl = Url },
            new TaxiRequestDriverDto { DisplayPhotoUrl = Url },
        ];
        foreach (var dto in dtos)
        {
            Assert.Equal(Url, Json(dto).GetProperty("displayPhotoUrl").GetString());
            Assert.False(Json(dto).TryGetProperty("platePhotoUrl", out _), dto.GetType().Name);
        }
        Assert.False(Json(new WayDriverDto()).TryGetProperty("displayPhotoUrl", out _));
        Assert.False(Json(new TaxiRequestDriverDto()).TryGetProperty("displayPhotoUrl", out _));
        Assert.True(Json(new TaxiBrowseDto()).TryGetProperty("displayPhotoUrl", out var nul) && nul.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void Owner_dto_carries_plate_display_and_lock()
    {
        MappingsOnce.Ensure();
        var taxi = new TaxiListing
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), DrivingLicenceNumberEnc = "x", DateOfBirth = new DateOnly(1985, 1, 1),
            Status = TaxiStatuses.PendingReview, EverApproved = true, PlatePhotoUrl = "https://p", DisplayPhotoUrl = Url,
            User = new User { Name = "Driver" },
        };
        var json = Json(taxi.Adapt<TaxiOwnerDto>());
        Assert.Equal("https://p", json.GetProperty("platePhotoUrl").GetString());
        Assert.Equal(Url, json.GetProperty("displayPhotoUrl").GetString());
        Assert.True(json.GetProperty("platePhotoLocked").GetBoolean());

        taxi.EverApproved = false;
        Assert.False(taxi.Adapt<TaxiOwnerDto>().PlatePhotoLocked);
        var pub = Json(taxi.Adapt<TaxiPublicDto>());
        Assert.Equal(Url, pub.GetProperty("displayPhotoUrl").GetString());
        Assert.False(pub.TryGetProperty("platePhotoUrl", out _));
    }

    [Fact]
    public void FeedItem_From_copies_display_photo()
        => Assert.Equal(Url, TaxiFeedItemDto.From(new TaxiBrowseDto { DisplayPhotoUrl = Url }).DisplayPhotoUrl);

    [Fact]
    public void Admin_dto_photos_have_labels_in_order()
    {
        var taxi = new TaxiListing
        {
            DrivingLicenceNumberEnc = "x", DateOfBirth = new DateOnly(1985, 1, 1), PlatePhotoUrl = "https://p", DisplayPhotoUrl = Url,
        };
        var dto = new AdminTaxiDto();
        AdminTaxiHandlers.AddPhotos(dto, taxi);
        Assert.Equal(
            [new AdminTaxiPhotoDto("Proof", "Proof (plate)", "https://p"), new AdminTaxiPhotoDto("Display", "Display", Url)],
            dto.Photos);
        var photos = Json(dto).GetProperty("photos");
        Assert.Equal("Proof (plate)", photos[0].GetProperty("label").GetString());
        Assert.Equal("Display", photos[1].GetProperty("kind").GetString());

        taxi.PlatePhotoUrl = null;
        AdminTaxiHandlers.AddPhotos(dto, taxi);
        Assert.Equal([new AdminTaxiPhotoDto("Display", "Display", Url)], dto.Photos);
    }

    [Fact]
    public void Display_photo_route_is_photo_limited()
        => Assert.Contains("UploadDisplayPhoto).RequireAuthorization(\"CanListTaxis\").DisableAntiforgery().OwnerPhotoLimited()",
            Src("RentNearBy.Api", "Endpoints", "TaxiEndpoints.cs"));

    [Fact]
    public void Display_photo_handler_never_sends_to_review()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "TaxiHandlers.cs"), "public static async Task<IResult> UploadDisplayPhoto(");
        foreach (var banned in new[] { "SendBackToReview", "PublishReviewRequestAsync", "golive.requested", "ListingInReviewAsync", "\"Status\" =" })
            Assert.DoesNotContain(banned, body);
        Assert.DoesNotContain("SET \"Status\"", TaxiHandlers.SetDisplayPhotoSql);
        Assert.Contains("\"UserId\" = @user", TaxiHandlers.SetDisplayPhotoSql);
        Assert.Contains("FOR UPDATE", TaxiHandlers.SetDisplayPhotoSql);
    }

    [Fact]
    public void Plate_handler_checks_lock_before_upload()
    {
        var body = Method(Src("RentNearBy.Api", "Handlers", "TaxiHandlers.cs"), "public static async Task<IResult> UploadPlatePhoto(");
        var locked = body.IndexOf("TaxiPhotoRules.ProofLocked(taxi)", StringComparison.Ordinal);
        Assert.True(locked >= 0);
        Assert.True(locked < body.IndexOf("SavePhotoAsync", StringComparison.Ordinal));
        Assert.True(locked < body.IndexOf("OpenReadStream", StringComparison.Ordinal));
        Assert.DoesNotContain("SendBackToReview", body);
        Assert.DoesNotContain("PublishReviewRequestAsync", body);
    }
}
