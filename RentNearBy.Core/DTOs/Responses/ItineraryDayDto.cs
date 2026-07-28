namespace RentNearBy.Core.DTOs.Responses;

// Single day entry within Service.ItineraryJson (a JSON-serialized List<ItineraryDayDto>) — no
// independent lifecycle/table of its own, deserialized in the handler after the Service Adapt.
public class ItineraryDayDto
{
    public int DayNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
