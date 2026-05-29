namespace RentNearBy.Core.Entities;

public class PlotPhoto
{
    public Guid Id { get; set; }
    public Guid PlotId { get; set; }
    public string PhotoUrl { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int PhotoOrder { get; set; }
    public DateTime UploadedAt { get; set; }

    public PlotListing PlotListing { get; set; } = null!;
}
