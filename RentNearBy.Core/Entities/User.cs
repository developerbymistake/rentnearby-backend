namespace RentNearBy.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string GoogleId { get; set; } = string.Empty;
    public string GoogleEmail { get; set; } = string.Empty;
    public string? ProfilePhotoUrl { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsPhoneVerified { get; set; } = false;
    public bool HasUsedFreePlan { get; set; } = false;
    public bool HasUsedFreePlotPlan { get; set; } = false;
    public bool IsContactVisible { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<Listing> Listings { get; set; } = new List<Listing>();
    public ICollection<UserMembership> Memberships { get; set; } = new List<UserMembership>();
    public ICollection<PlotMembership> PlotMemberships { get; set; } = new List<PlotMembership>();
    public ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();
}
