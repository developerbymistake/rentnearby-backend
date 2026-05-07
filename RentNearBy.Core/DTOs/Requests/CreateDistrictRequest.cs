namespace RentNearBy.Core.DTOs.Requests;

public class CreateDistrictRequest
{
    public string Name { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}
