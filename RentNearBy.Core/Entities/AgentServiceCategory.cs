namespace RentNearBy.Core.Entities;

// Composite-key many-to-many, exact shape of BannerDismissal — no surrogate Id. One Agent may serve
// multiple ServiceCategories.
public class AgentServiceCategory
{
    public Guid AgentId { get; set; }
    public Guid ServiceCategoryId { get; set; }

    public Agent Agent { get; set; } = null!;
    public ServiceCategory ServiceCategory { get; set; } = null!;
}
