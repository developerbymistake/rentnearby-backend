namespace RentNearBy.Core.DTOs.Requests;

// Step 1 (Vehicle, Contact, Base) in one save. The plate photo is uploaded separately after this.
public class UpsertTaxiBasicRequest
{
    public Guid? VehicleTypeId { get; set; }
    public short? Seats { get; set; }
    public string NumberPlate { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string WhatsAppNumber { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public Guid DistrictId { get; set; }
    public Guid? CityId { get; set; }
}
