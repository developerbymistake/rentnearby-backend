namespace RentNearBy.Core.DTOs.Requests;

public class CreatePlotRequest
{
    public decimal AreaValue { get; set; }
    public string AreaUnit { get; set; } = string.Empty;
    public string PlotType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string Address { get; set; } = string.Empty;
    public Guid DistrictId { get; set; }
    public Guid? CityId { get; set; }
}
