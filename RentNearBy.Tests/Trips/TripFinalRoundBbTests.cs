using FluentValidation.TestHelper;
using RentNearBy.Api.Handlers;
using RentNearBy.Api.Validators;
using RentNearBy.Core.DTOs.Requests;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Tests.Trips;

public class TripFinalRoundBbTests
{
    private static StayNearbyRoomDto Room(string name, short guests, short? floor = null) =>
        new() { RoomName = name, MaxGuests = guests, Floor = floor };

    [Fact]
    public void Best_room_is_the_first_that_fits_the_party()
    {
        var rooms = new List<StayNearbyRoomDto> { Room("Standard", 2), Room("Family Room", 4) };
        Assert.Equal("Standard", StayNearbyRooms.Best(rooms, 2)!.RoomName);
        Assert.Equal("Family Room", StayNearbyRooms.Best(rooms, 3)!.RoomName);
        Assert.Equal("Family Room", StayNearbyRooms.Best(rooms, 6)!.RoomName);
        Assert.Equal("Family Room", StayNearbyRooms.Best(rooms, null)!.RoomName);
        Assert.Null(StayNearbyRooms.Best(new List<StayNearbyRoomDto>(), 2));
    }

    [Fact]
    public void Ground_floor_filter_keeps_stays_with_a_ground_floor_room()
    {
        var items = new List<StayNearbyDto>
        {
            new() { Id = Guid.NewGuid(), HasGroundFloorRoom = true, Rooms = { Room("Deluxe", 2, 0) } },
            new() { Id = Guid.NewGuid(), HasGroundFloorRoom = false, Rooms = { Room("Suite", 3, 2) } },
        };
        var all = StayNearbyRooms.Apply(items, 2, groundFloorOnly: false);
        var ground = StayNearbyRooms.Apply(items, 2, groundFloorOnly: true);
        Assert.Equal(2, all.Count);
        Assert.Equal("Deluxe", Assert.Single(ground).BestRoomName);
        Assert.Equal(2, ground[0].BestRoomGuests);
    }

    [Fact]
    public void Rooms_json_parses_and_bad_json_is_empty()
    {
        var rooms = StayNearbyRooms.Parse("""[{"roomName":"Deluxe","maxGuests":2,"floor":0},{"roomName":"Suite","maxGuests":4,"floor":null}]""");
        Assert.Equal(2, rooms.Count);
        Assert.Equal((short)0, rooms[0].Floor);
        Assert.Null(rooms[1].Floor);
        Assert.Empty(StayNearbyRooms.Parse(null));
        Assert.Empty(StayNearbyRooms.Parse("not json"));
    }

    [Theory]
    [InlineData((short)0, true)]
    [InlineData((short)3, true)]
    [InlineData((short)4, false)]
    [InlineData((short)-1, false)]
    public void Room_floor_is_0_to_3(short floor, bool valid)
    {
        var request = new UpsertStayRoomsRequest
        {
            Rooms = { new StayRoomInput { PricePerNight = 2000, MaxGuests = 2, RoomName = "Deluxe", Meals = StayMeals.All[0], Floor = floor } },
        };
        var result = new UpsertStayRoomsRequestValidator().TestValidate(request);
        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void Room_without_floor_is_valid()
    {
        var request = new UpsertStayRoomsRequest
        {
            Rooms = { new StayRoomInput { PricePerNight = 2000, MaxGuests = 2, RoomName = "Deluxe", Meals = StayMeals.All[0] } },
        };
        new UpsertStayRoomsRequestValidator().TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Supply_reports_none_for_an_off_tab()
    {
        var supply = new TripCitySupplyDto { DistrictId = Guid.NewGuid(), Stays = 12, Drivers = 3 };
        var tabs = new List<AppTabDto>
        {
            new() { TabKey = AppTabKeys.Stays, IsActive = false },
            new() { TabKey = AppTabKeys.Taxi, IsActive = true },
        };
        var gated = TripHandlers.GatedSupply(supply, tabs);
        Assert.Equal(0, gated.Stays);
        Assert.Equal(3, gated.Drivers);
        Assert.Equal(12, supply.Stays);
        Assert.Equal(supply.DistrictId, gated.DistrictId);
    }

    [Fact]
    public void Problem_day_before_today_is_over()
    {
        var start = new DateOnly(2026, 11, 1);
        Assert.True(TripLifecycleHandlers.ProblemDayOver(start, 2, new DateOnly(2026, 11, 3)));
        Assert.False(TripLifecycleHandlers.ProblemDayOver(start, 2, new DateOnly(2026, 11, 2)));
        Assert.False(TripLifecycleHandlers.ProblemDayOver(start, 3, new DateOnly(2026, 11, 2)));
    }
}
