namespace RentNearBy.Core.Entities;

public class ListingLimitSetting
{
    public Guid Id { get; set; }

    // "Room" | "Plot" — a plain string discriminator, matching this codebase's existing convention
    // of not using native enums for entity state (see PaymentTransaction.Status).
    public string ListingKind { get; set; } = string.Empty;

    public int MaxListings { get; set; } = 5;
    public DateTime UpdatedAt { get; set; }
}
