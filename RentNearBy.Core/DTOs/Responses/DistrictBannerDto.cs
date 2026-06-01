namespace RentNearBy.Core.DTOs.Responses;

public class DistrictBannerDto
{
    public Guid Id { get; set; }
    public Guid DistrictId { get; set; }
    public string DistrictName { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string? ContactNumber { get; set; }
    public string? RedirectUrl { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
