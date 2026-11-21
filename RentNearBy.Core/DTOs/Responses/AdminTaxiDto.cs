namespace RentNearBy.Core.DTOs.Responses;

public class AdminTaxiDto : TaxiOwnerDto
{
    public Guid UserId { get; set; }
    public string? OwnerName { get; set; }
    public string? OwnerPhone { get; set; }
    public bool IsDeleted { get; set; }
    // Admin-only. The licence is decrypted on the detail endpoint only; null when absent or unreadable.
    public string? DrivingLicenceNumber { get; set; }
    public string? DateOfBirth { get; set; }   // yyyy-MM-dd
    public int? Age { get; set; }
    // Trip request standing (contract AD6). Rating fields come from the listing row; the offer counts and ringReady are
    // filled on the detail endpoint only and stay null in the list.
    public double? RatingAverage { get; set; }
    public int RatingCount { get; set; }
    public int RidesCompleted { get; set; }
    public int? OffersMissed { get; set; }
    public int? OffersAccepted { get; set; }
    public bool? RingReady { get; set; }
    // Owner roles A4: every route, hidden ones included, each with hiddenByPlan, and the plan line; detail only.
    public Owner.AdminPlanRowsDto? PlanRows { get; set; }
    public string? PlanState { get; set; }
}
