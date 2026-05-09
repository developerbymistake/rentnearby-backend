namespace RentNearBy.Core.DTOs.Requests;

public class CreateListingRequest
{
    public Guid RoomTypeId { get; set; }
    public string? Description { get; set; }
    public int PriceMonthly { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string Address { get; set; } = string.Empty;
    public Guid DistrictId { get; set; }
    public Guid? CityId { get; set; }
}
