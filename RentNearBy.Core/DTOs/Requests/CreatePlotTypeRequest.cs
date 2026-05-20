namespace RentNearBy.Core.DTOs.Requests;

public class CreatePlotTypeRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; } = 999;
}
