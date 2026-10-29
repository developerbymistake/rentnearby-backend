namespace RentNearBy.Core.DTOs.Responses;

public class AdminStayJobDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string DepartmentName { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public int MinimumExperienceYears { get; set; }
    public string? ContactPhone { get; set; }
    public string? WhatsAppNumber { get; set; }
    public int MinSalary { get; set; }
    public int MaxSalary { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Guid StayListingId { get; set; }
    public string? StayName { get; set; }
    public string? StayStatus { get; set; }
    public bool StayIsDeleted { get; set; }
    public Guid OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public string? DistrictName { get; set; }
    public string? CityName { get; set; }
}
