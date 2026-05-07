namespace RentNearBy.Core.DTOs.Responses;

public class CityDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}
