namespace RentNearBy.Core.Entities;

// Which document a stay's registration number belongs to. Deactivate instead of delete once in use.
public class StayRegistrationType
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? HelpText { get; set; }
    public int SortOrder { get; set; } = 999;
    public bool RequiresOtherName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<StayListing> StayListings { get; set; } = new List<StayListing>();
}
