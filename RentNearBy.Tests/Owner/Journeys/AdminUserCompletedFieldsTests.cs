using System.Text.Json;
using RentNearBy.Core.DTOs.Responses;
using RentNearBy.Tests.Trips;

namespace RentNearBy.Tests.Owner.Journeys;

// FB-W3 / JT-B-CNT-4: the counters reach only the admin user detail, the one that matches the role.
public class AdminUserCompletedFieldsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Json(object dto) => JsonSerializer.SerializeToElement(dto, dto.GetType(), Web);

    [Fact]
    public void List_item_has_neither_key()
    {
        var json = Json(new AdminUserDto { Role = UserRoles.Seeker });
        Assert.False(json.TryGetProperty("completedTrips", out _));
        Assert.False(json.TryGetProperty("completedBookings", out _));
    }

    [Fact]
    public void Seeker_detail_has_completed_trips_only()
    {
        var dto = new AdminUserDto { Role = UserRoles.Seeker };
        dto.SetCompletedCounters(UserRoles.Seeker, 3, 7);
        var json = Json(dto);
        Assert.Equal(3, json.GetProperty("completedTrips").GetInt32());
        Assert.False(json.TryGetProperty("completedBookings", out _));
    }

    [Theory]
    [InlineData(UserRoles.StayOwner)]
    [InlineData(UserRoles.TaxiDriver)]
    public void Owner_detail_has_completed_bookings_only(string role)
    {
        var dto = new AdminUserDto { Role = role };
        dto.SetCompletedCounters(role, 3, 7);
        var json = Json(dto);
        Assert.Equal(7, json.GetProperty("completedBookings").GetInt32());
        Assert.False(json.TryGetProperty("completedTrips", out _));
    }

    [Fact]
    public void Zero_is_shown_not_hidden()
    {
        var dto = new AdminUserDto { Role = UserRoles.Seeker };
        dto.SetCompletedCounters(UserRoles.Seeker, 0, 0);
        Assert.Equal(0, Json(dto).GetProperty("completedTrips").GetInt32());
    }

    [Fact]
    public void Me_dto_has_no_counter()
    {
        var json = Json(new UserDto());
        Assert.False(json.TryGetProperty("completedTrips", out _));
        Assert.False(json.TryGetProperty("completedBookings", out _));
    }

    [Fact]
    public void Admin_handler_sets_the_counters_only_in_the_detail()
    {
        var src = File.ReadAllText(Path.Combine(CompletionCounterGuardTests.Root(), "RentNearBy.Api", "Handlers", "AdminHandlers.cs"));
        var detail = src.IndexOf("public static async Task<IResult> GetUserById", StringComparison.Ordinal);
        var call = src.IndexOf("SetCompletedCounters", StringComparison.Ordinal);
        Assert.True(detail > 0 && call > detail);
        Assert.Equal(call, src.LastIndexOf("SetCompletedCounters", StringComparison.Ordinal));
    }
}
