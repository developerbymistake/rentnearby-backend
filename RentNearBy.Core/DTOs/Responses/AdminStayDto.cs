namespace RentNearBy.Core.DTOs.Responses;

public class AdminStayDto : StayOwnerDto
{
    public Guid UserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    // Admin-only.
    public string? RegistrationNumber { get; set; }
    public string? Gstin { get; set; }
    // Flags only; the UPI ID and the WhatsApp number never reach the admin.
    public bool HasUpiId { get; set; }
    public bool HasShareWhatsApp { get; set; }
    // Owner roles A4: every photo and room, hidden ones included, each with hiddenByPlan; detail only.
    public Owner.AdminPlanRowsDto? PlanRows { get; set; }
    // "Free · Plus ended" style plan line of the review screen (o_pl_19); detail only.
    public string? PlanState { get; set; }
    // Owner roles A5: the biggest active "Lower" special price percent of the stay; list rows only.
    public int? MaxLowerPercent { get; set; }
}
