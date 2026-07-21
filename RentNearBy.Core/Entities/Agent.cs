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

    // Years of experience, admin-entered, shown to consumers on their assigned-agent card to build
    // trust — optional since it may not be known/entered at creation time.
    public int? Experience { get; set; }

    // The consumer account this Agent logs in as — an Agent is a role on an existing User, not a
    // separate identity (no separate Agent login/session exists). Nullable at the DB/entity level
    // only for migration safety; the API requires it on create and never allows changing it after
    // (delete-and-recreate if the wrong account was linked, same convention as
    // ServiceCategory.ServiceSectionId). Enforced unique via a partial index in OnModelCreating.
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public ICollection<AgentServiceCategory> AgentServiceCategories { get; set; } = new List<AgentServiceCategory>();
}
