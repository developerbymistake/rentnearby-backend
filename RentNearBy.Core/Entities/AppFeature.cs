namespace RentNearBy.Core.Entities;

public class AppFeature
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int FreeLimit { get; set; } = 1;
    public int FreeDays { get; set; } = 2;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
