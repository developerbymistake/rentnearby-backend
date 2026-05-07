namespace RentNearBy.Core.DTOs.Requests;

public class CreateCityRequest
{
    public Guid DistrictId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}
