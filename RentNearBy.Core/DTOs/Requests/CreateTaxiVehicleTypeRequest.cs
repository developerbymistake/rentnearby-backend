namespace RentNearBy.Core.DTOs.Requests;

public class CreateTaxiVehicleTypeRequest
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public short? DefaultSeats { get; set; }
    public int SortOrder { get; set; } = 999;
}
