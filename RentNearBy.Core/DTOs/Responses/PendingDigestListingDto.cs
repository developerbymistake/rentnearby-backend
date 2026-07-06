namespace RentNearBy.Core.DTOs.Responses;

public class PendingDigestListingDto
{
    public Guid Id { get; set; }
    public Guid DistrictId { get; set; }
    public string DistrictName { get; set; } = string.Empty;
}
