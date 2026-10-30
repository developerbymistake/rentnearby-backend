namespace RentNearBy.Core.DTOs.Requests;

// Code is derived from Name (StayRegistrationRules.CodeOf) and never changes afterwards.
public class CreateStayRegistrationTypeRequest
{
    public string Name { get; set; } = string.Empty;
    public string? HelpText { get; set; }
    public int SortOrder { get; set; } = 999;
    public bool RequiresOtherName { get; set; }
}
