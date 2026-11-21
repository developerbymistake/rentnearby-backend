namespace RentNearBy.Core.DTOs.Buddy;

// Trip limit per traveller: owned trips + trips joined as a viewer, oldest/ended first, at most Limit rows.
public class TripSlotsDto
{
    public int Limit { get; set; }
    public int Count { get; set; }
    public List<TripSlotTripDto> Trips { get; set; } = [];
}

public class TripSlotTripDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? OwnerName { get; set; }
}
