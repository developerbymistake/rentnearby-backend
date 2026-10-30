namespace RentNearBy.Core.Entities;

// One stay per owner while not deleted (ix_staylistings_user_live). Step fields stay nullable while
// the stay is a Draft; StaySetupRules derives the step flags from them.
public class StayListing
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public string? Name { get; set; }
    public Guid? StayTypeId { get; set; }
    public string? VehicleAccess { get; set; }
    public bool? HostLivesHere { get; set; }
    public string[] Facilities { get; set; } = [];
    public string? Description { get; set; }

    // Admin-only registration details; RegistrationTypeName is a snapshot of the type's name.
    public required Guid RegistrationTypeId { get; set; }
    public required string RegistrationTypeName { get; set; }
    public string? RegistrationOtherName { get; set; }
    public required string RegistrationNumber { get; set; }
    public string? Gstin { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? Address { get; set; }
    public Guid? DistrictId { get; set; }
    public Guid? CityId { get; set; }

    public TimeOnly? CheckInTime { get; set; }
    public TimeOnly? CheckOutTime { get; set; }
    public bool PetsAllowed { get; set; }
    public bool SmokingAllowed { get; set; }
    public bool AlcoholAllowed { get; set; }
    public bool UnmarriedCouplesAllowed { get; set; }
    public bool IdProofRequired { get; set; }
    public string? CancellationPolicy { get; set; }
    public DateTime? RulesSavedAt { get; set; }

    public string Status { get; set; } = "Draft";
    // Set on admin approval, never cleared: keeps the role locked after a later reject or delete.
    public bool EverApproved { get; set; }
    public bool AcceptingGuests { get; set; } = true;
    public string? RejectedReason { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedByAdminId { get; set; }

    // Cheapest room, rewritten on every rooms/price write.
    public int? MinPricePerNight { get; set; }
    public string? Slug { get; set; }
    // Copied from the owner's plan; public ordering reads these, never UserPlans.
    public short PriorityRank { get; set; }
    public bool IsFeatured { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    public User User { get; set; } = null!;
    public StayType? StayType { get; set; }
    public StayRegistrationType RegistrationType { get; set; } = null!;
    public District? District { get; set; }
    public City? City { get; set; }
    public ICollection<StayRoom> Rooms { get; set; } = new List<StayRoom>();
    public ICollection<StayPhoto> Photos { get; set; } = new List<StayPhoto>();
}
