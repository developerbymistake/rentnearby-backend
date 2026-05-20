namespace RentNearBy.Core.DTOs.Requests;

public class UpdatePlotRequest
{
    public decimal? AreaValue { get; set; }
    public string? AreaUnit { get; set; }
    public Guid? PlotTypeId { get; set; }
    public string? Description { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? Address { get; set; }
    public Guid? CityId { get; set; }
    public bool? IsActive { get; set; }
}
