namespace RentNearBy.Core.Entities;

// A city pair the admin shows in the Taxi tab. Names are the snapshot taken at create; the taxis are counted by city keys.
public class PopularRoute
{
    public Guid Id { get; set; }
    public long FromCityKey { get; set; }
    public long ToCityKey { get; set; }
    public string FromCityName { get; set; } = string.Empty;
    public string ToCityName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
