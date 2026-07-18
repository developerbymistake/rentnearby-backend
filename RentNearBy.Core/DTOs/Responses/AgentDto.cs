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
    public List<Guid> ServiceCategoryIds { get; set; } = new();
    public List<string> ServiceCategoryNames { get; set; } = new();
}
