namespace RentNearBy.Core.DTOs.Requests;

public class CreateRoomTypeRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; } = 999;
}
