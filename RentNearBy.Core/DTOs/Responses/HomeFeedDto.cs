namespace RentNearBy.Core.DTOs.Responses;

// GET /home/feed. A null section is off (tab inactive) or was not requested; [] means on but empty.
public class HomeFeedDto
{
    public HomeFeedTabsDto Tabs { get; set; } = new();
    public List<StayBrowseDto>? Stays { get; set; }
    public List<TaxiBrowseDto>? Taxis { get; set; }
    public List<StayBrowseDto>? RecentStays { get; set; }
}

public class HomeFeedTabsDto
{
    public bool Stays { get; set; }
    public bool Jobs { get; set; }
    public bool Taxi { get; set; }
}
