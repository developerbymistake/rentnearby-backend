namespace RentNearBy.Core.DTOs.Responses;

public class StateDto
{
    public string Name { get; set; } = string.Empty;
    public int TotalDistricts { get; set; }
    public int ActiveDistricts { get; set; }
}
