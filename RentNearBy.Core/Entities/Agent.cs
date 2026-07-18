namespace RentNearBy.Core.Entities;

// Phone and WhatsAppNumber are deliberately separate fields — the consumer app shows two distinct
// Call/WhatsApp buttons using agent.phone/agent.whatsAppNumber respectively.
public class Agent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string WhatsAppNumber { get; set; } = string.Empty;
    public string PhotoUrl { get; set; } = string.Empty;
    public string PhotoFilePath { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ICollection<AgentServiceCategory> AgentServiceCategories { get; set; } = new List<AgentServiceCategory>();
}
