namespace RentNearBy.Core.DTOs.Responses;

public class DistrictDto
{
    public Guid Id { get; set; }
    public Guid CityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}
