namespace RentNearBy.Core.DTOs.Responses;

public class InclusionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}
