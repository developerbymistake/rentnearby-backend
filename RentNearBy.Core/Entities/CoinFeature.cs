namespace RentNearBy.Core.Entities;

// Small, seed-only catalog of "what coins can be spent on" — Go-Live today, future coin-gated
// features (contact reveal, chat, etc.) later, each as a new row here, never a schema change.
public class CoinFeature
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;            // "ROOM_GOLIVE" | "PLOT_GOLIVE"
    public string DisplayName { get; set; } = string.Empty;    // "Room Go-Live" — admin-facing
    public string QuotaUnitLabel { get; set; } = string.Empty; // "rooms" | "plots" — for UI/error text
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
