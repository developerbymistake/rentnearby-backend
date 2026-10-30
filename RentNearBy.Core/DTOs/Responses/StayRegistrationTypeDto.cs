namespace RentNearBy.Core.DTOs.Responses;

public class StayRegistrationTypeDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? HelpText { get; set; }
    public int SortOrder { get; set; }
    public bool RequiresOtherName { get; set; }
    public bool IsActive { get; set; }
    public bool IsSeeded { get; set; }
}
