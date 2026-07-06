namespace RentNearBy.Infrastructure.Services;

public class DistrictDigestMessage
{
    public Guid DistrictId { get; set; }
    public string DistrictName { get; set; } = string.Empty;
    public int RoomCount { get; set; }
    public int PlotCount { get; set; }
}
