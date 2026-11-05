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

    // Per request (the cache stays party-free). With guests: the party quote (StayPartyFit) sets fits, the quote and the
    // best room (the quote line seating the most guests); a stay that does not fit keeps the single-room pick.
    public static List<StayNearbyDto> Apply(List<StayNearbyDto> items, int? guests, bool groundFloorOnly, int? roomsWanted = null, int nights = 1)
    {
        var result = groundFloorOnly ? items.Where(i => i.HasGroundFloorRoom).ToList() : items;
        foreach (var item in result)
        {
            var rooms = item.Rooms ?? new List<StayNearbyRoomDto>();
            var best = Best(rooms, guests);
            item.Fits = null;
            item.QuotePerNight = null;
            item.QuoteTotal = null;
            item.QuoteLines = null;
            if (guests is { } g && g > 0)
            {
                var quote = StayPartyFit.Quote(
                    rooms.Select(r => new StayFitRoom(r.RoomName, r.MaxGuests, r.PricePerNight, r.Floor)).ToList(),
                    g, roomsWanted, groundFloorOnly, nights);
                item.Fits = quote.Fits;
                if (quote.Fits)
                {
                    item.QuotePerNight = quote.PerNight;
                    item.QuoteTotal = quote.Total;
                    item.QuoteLines = quote.Lines
                        .Select(l => new StayQuoteLineDto { RoomName = l.RoomName, Units = l.Units, Guests = l.Guests, PricePerNight = l.PricePerNight })
                        .ToList();
                    var top = quote.Lines.OrderByDescending(l => l.Guests).First();
                    best = rooms.FirstOrDefault(r => r.RoomName == top.RoomName) ?? best;
                }
            }
            item.BestRoomName = best?.RoomName;
            item.BestRoomGuests = best?.MaxGuests;
        }
        return result;
    }
}
