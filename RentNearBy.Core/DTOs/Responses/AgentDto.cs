namespace RentNearBy.Core.DTOs.Responses;

// ServiceCategoryIds/Names are flattened from the AgentServiceCategory join (per-agent category
// multi-select), matching CoinTransactionWithUserResponse's Id+Name flattening convention.
public class AgentDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string WhatsAppNumber { get; set; } = string.Empty;
    public string PhotoUrl { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? Experience { get; set; }
    // The linked consumer account this Agent logs in as — always set in practice (required on
    // create, never removable), denormalized here so the admin panel can show who it is without a
    // second lookup.
    public Guid? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserPhoneNumber { get; set; }
    public List<Guid> ServiceCategoryIds { get; set; } = new();
    public List<string> ServiceCategoryNames { get; set; } = new();
}
