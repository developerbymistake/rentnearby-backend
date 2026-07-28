namespace RentNearBy.Core.Entities;

// Generic, reusable key-value settings master table, keyed by Type — e.g. the itinerary disclaimer text.
// Distinct from AppFeatureFlag (which is specifically shaped for bool+int feature toggles like the
// payment kill switch) — new setting types just add a new Type constant here, never a new entity/table.
public class AppSetting
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public Guid? UpdatedByAdminId { get; set; }
    public Admin? UpdatedByAdmin { get; set; }
    public DateTime UpdatedAt { get; set; }
}
