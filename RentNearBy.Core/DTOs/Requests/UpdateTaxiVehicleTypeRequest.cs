namespace RentNearBy.Core.DTOs.Requests;

// Every field optional; DefaultSeats = 0 clears it.
public class UpdateTaxiVehicleTypeRequest
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public short? DefaultSeats { get; set; }
    public int? SortOrder { get; set; }
    public bool? IsActive { get; set; }
}
