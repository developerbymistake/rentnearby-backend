namespace RentNearBy.Core.DTOs.Responses;

public class TaxiVehicleTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public short? DefaultSeats { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public bool IsSeeded { get; set; }
}
