namespace RentNearBy.Core.DTOs.Requests;

// Every field optional; HelpText = "" clears it.
public class UpdateStayRegistrationTypeRequest
{
    public string? Name { get; set; }
    public string? HelpText { get; set; }
    public int? SortOrder { get; set; }
    public bool? RequiresOtherName { get; set; }
    public bool? IsActive { get; set; }
}
