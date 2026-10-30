namespace RentNearBy.Core.DTOs.Requests;

public class UpsertStayBasicRequest
{
    public string Name { get; set; } = string.Empty;
    public Guid? StayTypeId { get; set; }
    public string VehicleAccess { get; set; } = string.Empty;
    public bool? HostLivesHere { get; set; }
    public List<string>? Facilities { get; set; }
    public string? Description { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string Address { get; set; } = string.Empty;
    public Guid DistrictId { get; set; }
    public Guid? CityId { get; set; }
    public Guid? RegistrationTypeId { get; set; }
    public string? RegistrationOtherName { get; set; }
    // null = keep the stored value; GSTIN "" clears it.
    public string? RegistrationNumber { get; set; }
    public string? Gstin { get; set; }
}
