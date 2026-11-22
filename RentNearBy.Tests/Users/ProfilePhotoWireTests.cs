using System.Text.Json;
using Mapster;
using RentNearBy.Api.Mappings;
using RentNearBy.Core.DTOs.Buddy;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Core.Models.Buddy;
using RentNearBy.Core.Utils;

namespace RentNearBy.Tests.Users;

// CONTRACT_BACKEND_DP 7.4: wire names of the profile picture fields and the person thumb.
public class ProfilePhotoWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string Stored = "https://res.cloudinary.com/demo/image/upload/v1/bakhli/user_1/profile/x.jpg";

    private static JsonElement Json(object dto) => JsonDocument.Parse(JsonSerializer.Serialize(dto, dto.GetType(), Web)).RootElement;

    private static UserDto Map(string? url, DateTime? updatedAt)
    {
        DtoMappings.ConfigureMappings();
        return new User { Id = Guid.NewGuid(), PhoneNumber = "9876543210", ProfilePhotoUrl = url, ProfilePhotoPath = url == null ? null : "p", ProfilePhotoUpdatedAt = updatedAt }
            .Adapt<UserDto>();
    }

    [Fact]
    public void User_dto_always_carries_the_photo_key()
    {
        var none = Json(Map(null, null));
        Assert.True(none.TryGetProperty("profilePhotoUrl", out var nullUrl));
        Assert.Equal(JsonValueKind.Null, nullUrl.ValueKind);
        Assert.False(none.GetProperty("profilePhotoRemoved").GetBoolean());

        var set = Json(Map(Stored, DateTime.UtcNow));
        Assert.Equal(Stored, set.GetProperty("profilePhotoUrl").GetString());
        Assert.False(set.GetProperty("profilePhotoRemoved").GetBoolean());
    }

    [Fact]
    public void Removed_only_when_the_admin_cleared_it()
    {
        Assert.True(Map(null, DateTime.UtcNow).ProfilePhotoRemoved);
        Assert.False(Map(null, null).ProfilePhotoRemoved);
        Assert.False(Map(Stored, DateTime.UtcNow).ProfilePhotoRemoved);
    }

    public static TheoryData<object, string> PersonShapes => new()
    {
        { new PersonDto { PhotoUrl = "u" }, "photoUrl" },
        { new MoneyPersonDto { PhotoUrl = "u" }, "photoUrl" },
        { new TravellerChipDto { PhotoUrl = "u" }, "photoUrl" },
        { new FoundPersonDto { PhotoUrl = "u" }, "photoUrl" },
        { new BuddyItemDto { PhotoUrl = "u" }, "photoUrl" },
        { new BlockedPersonDto { PhotoUrl = "u" }, "photoUrl" },
        { new TripJoinedPersonDto { PhotoUrl = "u" }, "photoUrl" },
        { new TripInvitedPersonDto { PhotoUrl = "u" }, "photoUrl" },
        { new TripOwnerRefDto { PhotoUrl = "u" }, "photoUrl" },
        { new ConversationDto { OtherPartyPhotoUrl = "u" }, "otherPartyPhotoUrl" },
        { new AdminUserDto { ProfilePhotoUrl = "u" }, "profilePhotoUrl" },
    };

    [Theory]
    [MemberData(nameof(PersonShapes))]
    public void Person_shapes_serialise_the_photo_in_camel_case(object dto, string key)
    {
        Assert.Equal("u", Json(dto).GetProperty(key).GetString());
    }

    [Fact]
    public void Person_thumb_is_inserted_once_and_never_on_other_hosts()
    {
        var thumb = CloudinaryThumb.Apply(Stored, CloudinaryThumb.PersonFace);
        Assert.Equal("https://res.cloudinary.com/demo/image/upload/c_fill,g_face,w_160,h_160,q_auto,f_auto/v1/bakhli/user_1/profile/x.jpg", thumb);
        Assert.Equal(thumb, CloudinaryThumb.Apply(thumb, CloudinaryThumb.PersonFace));
        Assert.Equal("https://example.com/a.jpg", CloudinaryThumb.Apply("https://example.com/a.jpg", CloudinaryThumb.PersonFace));
        Assert.Null(CloudinaryThumb.Apply(null, CloudinaryThumb.PersonFace));
        Assert.Null(CloudinaryThumb.Apply("  ", CloudinaryThumb.PersonFace));
        Assert.Equal("c_fill,g_face,w_160,h_160,q_auto,f_auto", CloudinaryThumb.PersonFace);
    }

    [Fact]
    public void Money_person_carries_the_traveller_photo_and_a_deleted_person_none()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var traveller = new TripTraveller(other, "Asha", "Bakhli#12345678", false, TripMemberStatuses.Joined, DateTime.UtcNow, "thumb");
        Assert.Equal("thumb", MoneyPeople.Person(traveller, other, me).PhotoUrl);
        Assert.Null(MoneyPeople.Person(null, other, me).PhotoUrl);
        Assert.Null(MoneyPeople.Person(traveller with { PhotoUrl = null }, other, me).PhotoUrl);
    }
}
