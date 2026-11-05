using System.Text.Json;
using RentNearBy.Core.DTOs.Responses;

namespace RentNearBy.Core.Models;

public static class StayNearbyRooms
{
    public const short GroundFloor = 0;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Rooms as the nearby SQL aggregates them (json_agg ordered by RoomIndex); bad or empty JSON = no rooms.
    public static List<StayNearbyRoomDto> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<StayNearbyRoomDto>();
        try { return JsonSerializer.Deserialize<List<StayNearbyRoomDto>>(json, Json) ?? new List<StayNearbyRoomDto>(); }
        catch (JsonException) { return new List<StayNearbyRoomDto>(); }
    }

    // MaxGuests 4 means "4+", so it fits any party of 4 or more.
    public static StayNearbyRoomDto? Best(IReadOnlyList<StayNearbyRoomDto> rooms, int? guests)
    {
        if (rooms.Count == 0) return null;
        if (guests is { } g && g > 0)
        {
            var need = Math.Min(g, 4);
            var fit = rooms.FirstOrDefault(r => r.MaxGuests >= need);
            if (fit != null) return fit;
        }
        return rooms.OrderByDescending(r => r.MaxGuests).First();
    }

    public static List<StayNearbyDto> Apply(List<StayNearbyDto> items, int? guests, bool groundFloorOnly)
    {
        var result = groundFloorOnly ? items.Where(i => i.HasGroundFloorRoom).ToList() : items;
        foreach (var item in result)
        {
            var best = Best(item.Rooms ?? new List<StayNearbyRoomDto>(), guests);
            item.BestRoomName = best?.RoomName;
            item.BestRoomGuests = best?.MaxGuests;
        }
        return result;
    }
}
