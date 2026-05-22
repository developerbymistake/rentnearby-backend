namespace RentNearBy.Core.DTOs.Responses;

public class DistrictDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StateName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
